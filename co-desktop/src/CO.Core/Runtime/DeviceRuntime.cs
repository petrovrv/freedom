// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CO.Core.Crypto;
using CO.Core.Files;
using CO.Core.Host;
using CO.Core.Pairing;
using CO.Core.Policy;
using CO.Core.Protocol;
using CO.Core.Tools;
using CO.Core.Transport;
using Google.Apis.Auth.OAuth2;

namespace CO.Core.Runtime;

public interface IDeviceKeyStore
{
    ISigner SigningKey();              // ECDSA P-256, result signatures
    RSA ServiceAccountKey();           // RSA 2048, service account JWT
    bool HardwareBacked { get; }
}

public sealed class SoftwareKeyStore : IDeviceKeyStore
{
    private readonly EcdsaSigner _sig = EcdsaSigner.CreateEphemeral();
    private readonly RSA _rsa = RSA.Create(2048);
    public ISigner SigningKey() => _sig;
    public RSA ServiceAccountKey() => _rsa;
    public bool HardwareBacked => false;
}

// bridge.json in the device folder: created empty by Runtime, filled by Bridge (TZ §6.2).
public sealed class BridgeInfo
{
    [JsonPropertyName("state")] public string State { get; set; } = "";          // "" | pending | paired | revoked
    [JsonPropertyName("hostId")] public string HostId { get; set; } = "";
    [JsonPropertyName("projectId")] public string ProjectId { get; set; } = "";
    [JsonPropertyName("ownerEmail")] public string OwnerEmail { get; set; } = "";
    [JsonPropertyName("bridgePublicKey")] public string BridgePublicKey { get; set; } = "";
    // Key rotation: signature of the new key by the previous one, over "co.bridge.key.v1\n<newKey>".
    [JsonPropertyName("previousKeySignature")] public string? PreviousKeySignature { get; set; }
    [JsonPropertyName("serviceAccount")] public string ServiceAccount { get; set; } = "";
    [JsonPropertyName("serviceAccountKeyId")] public string ServiceAccountKeyId { get; set; } = "";
    // 0.9.8: auto-approval granted by the owner (Bridge 1.9.2 devices.trustedEnvironments), signed by the Bridge key
    // over "co.device.trust.v2\n<deviceId>\n1\n<trustUntil>"; valid until trustUntil (2 days, renewed daily);
    // checked in DeviceRuntime.OwnerTrusted.
    [JsonPropertyName("trusted")] public bool Trusted { get; set; }
    [JsonPropertyName("trustUntil")] public string? TrustUntil { get; set; }
    [JsonPropertyName("trustSig")] public string? TrustSig { get; set; }
}

public sealed class DeviceState
{
    [JsonPropertyName("deviceId")] public string DeviceId { get; set; } = "";
    [JsonPropertyName("short")] public string Short { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = Environment.MachineName;
    [JsonPropertyName("environmentId")] public string EnvironmentId { get; set; } = "";
    [JsonPropertyName("account")] public string Account { get; set; } = "";
    [JsonPropertyName("ownerEmail")] public string OwnerEmail { get; set; } = InstallDefaults.OwnerEmail;
    [JsonPropertyName("folder")] public DeviceFolderIds? Folder { get; set; }
    [JsonPropertyName("pairingRequestFileId")] public string PairingRequestFileId { get; set; } = "";
    [JsonPropertyName("bridgeFileId")] public string BridgeFileId { get; set; } = "";
    [JsonPropertyName("heartbeatFileId")] public string HeartbeatFileId { get; set; } = "";
    // Created by the device, filled by Bridge with the signed release (TZ §6.5).
    [JsonPropertyName("updateManifestFileId")] public string UpdateManifestFileId { get; set; } = "";
    [JsonPropertyName("updateBinFileId")] public string UpdateBinFileId { get; set; } = "";
    [JsonPropertyName("bridge")] public BridgeInfo? Bridge { get; set; }        // pinned at first sight
    [JsonPropertyName("managed")] public bool Managed { get; set; }
    [JsonPropertyName("trusted")] public bool Trusted { get; set; }   // owner: auto-approve all agent actions (no confirmation prompts)
    // owner: one Google token with drive + cloud-platform + pubsub serves the device, pairing and CO Web;
    // member: drive.file only (TZ §6.1).
    [JsonPropertyName("role")] public string Role { get; set; } = "";

    [JsonIgnore] public bool Paired => Bridge?.State == "paired" && Bridge.ServiceAccount.Length > 0;
}

public sealed class DeviceRuntime : IAsyncDisposable
{
    public const string Version = "0.12.0";
    private readonly string _dataDir;
    private readonly IDeviceKeyStore _keys;
    private readonly IDriveFiles _drive;
    private readonly IApprovalService _approvals;
    private readonly List<IDeviceChannel> _channels = new();
    private CancellationTokenSource? _cts;

    public DeviceState State { get; private set; }
    public FolderRegistry Folders { get; }
    public GrantStore Grants { get; }
    public SnapshotStore Snapshots { get; }
    public AuditLog Audit { get; }
    public ToolCatalog Catalog { get; } = new();
    public ToolHost? Host { get; private set; }
    public PubSubChannel? PubSub { get; private set; }
    public DriveMailboxChannel? DriveChannel { get; private set; }

    public DeviceUpdater Updater { get; }
    // Mac resident: the Windows installer channel is not used (false skips update files and the update loop).
    public bool AutoUpdate { get; set; } = true;
    // false: calls come only through the Drive mailbox (the Mac resident's safe start after repeated crashes).
    public bool UsePubSub { get; set; } = true;
    // co-resident 0.3.3: Pub/Sub over REST (HTTP/1.1) instead of gRPC; the Mac resident sets it.
    public bool PubSubRest { get; set; }
    public event Action<string>? StatusChanged;
    public event Action<UpdateReady>? UpdateReady;

    public DeviceRuntime(string dataDir, IDeviceKeyStore keys, IDriveFiles drive, IApprovalService approvals)
    {
        _dataDir = dataDir;
        _keys = keys;
        _drive = drive;
        _approvals = approvals;
        Directory.CreateDirectory(dataDir);
        State = File.Exists(StateFile) ? JsonSerializer.Deserialize<DeviceState>(File.ReadAllText(StateFile))! : new DeviceState();
        if (State.DeviceId.Length == 0)
        {
            State.DeviceId = PairingMath.NewDeviceId();
            State.Short = MakeShort(Environment.MachineName);
            Save();
        }
        Folders = new FolderRegistry(Path.Combine(dataDir, "folders.json"));
        Grants = new GrantStore(Path.Combine(dataDir, "grants.json"));
        Snapshots = new SnapshotStore(Path.Combine(dataDir, "snapshots"));
        Audit = new AuditLog(Path.Combine(dataDir, "audit", "audit.jsonl"));
        foreach (var t in FsTools.All()) Catalog.Add(t);
        Catalog.Add(new ApproveTool());
        Updater = new DeviceUpdater(drive, dataDir);
    }

    private string StateFile => Path.Combine(_dataDir, "device.json");

    public void Save()
    {
        string tmp = StateFile + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(State, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, StateFile, true);
    }

    public static string MakeShort(string machine)
    {
        string s = new string(machine.ToLowerInvariant().Where(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c)).ToArray());
        if (s.Length == 0 || !char.IsAsciiLetterLower(s[0])) s = "pc" + s;
        return s.Length > ToolNames.MaxShort ? s[..ToolNames.MaxShort] : s;
    }

    // Step 1-4 of TZ §6.2 on the device: folder, placeholders, pairing request.
    public async Task CreatePairingRequestAsync(string account, string environmentId, CancellationToken ct)
    {
        State.Account = account;
        State.EnvironmentId = environmentId;
        bool isOwner = account.Equals(State.OwnerEmail, StringComparison.OrdinalIgnoreCase);
        State.Folder ??= await DeviceFolder.CreateAsync(_drive, State.DeviceId, isOwner ? null : State.OwnerEmail, ct);
        if (State.BridgeFileId.Length == 0)
            State.BridgeFileId = await _drive.CreateFileAsync("bridge.json", State.Folder.FolderId, Encoding.UTF8.GetBytes("{}"), "application/json", ct);
        if (State.HeartbeatFileId.Length == 0)
            State.HeartbeatFileId = await _drive.CreateFileAsync("heartbeat.json", State.Folder.FolderId, Encoding.UTF8.GetBytes("{}"), "application/json", ct);

        byte[] req = Encoding.UTF8.GetBytes(Wire.Serialize(BuildRequest()));
        if (State.PairingRequestFileId.Length == 0)
            State.PairingRequestFileId = await _drive.CreateFileAsync("pairing-request.json", State.Folder.FolderId, req, "application/json", ct);
        else
            await _drive.UpdateFileAsync(State.PairingRequestFileId, req, "application/json", ct);
        Save();
        StatusChanged?.Invoke("pairing request written");
    }

    public PairingRequest BuildRequest() => new()
    {
        DeviceId = State.DeviceId, Short = State.Short, Name = State.Name,
        Os = Environment.OSVersion.VersionString, Version = Version,
        EnvironmentId = State.EnvironmentId, Account = State.Account,
        DeviceFolderId = State.Folder?.FolderId ?? "", SlotIds = State.Folder?.SlotIds ?? new(),
        ResultsFolderId = State.Folder?.ResultsId ?? "",
        Tools = Catalog.ToMcpList(), SigningKey = _keys.SigningKey().PublicKey,
        ServiceAccountCert = PairingMath.SelfSignedCertPem(_keys.ServiceAccountKey(), State.DeviceId),
        CreatedUtc = Wire.Now(DateTime.UtcNow), Managed = State.Managed,
    };

    // Reads bridge.json; pins the Bridge key at first sight, accepts a new key only when signed by the pinned one.
    public async Task<BridgeInfo?> RefreshBridgeInfoAsync(CancellationToken ct)
    {
        if (State.BridgeFileId.Length == 0) return null;
        byte[] raw = await _drive.DownloadAsync(State.BridgeFileId, ct);
        BridgeInfo? info;
        try { info = JsonSerializer.Deserialize<BridgeInfo>(raw); } catch (JsonException) { return State.Bridge; }
        if (info == null || info.BridgePublicKey.Length == 0) return State.Bridge;

        if (State.Bridge == null || State.Bridge.BridgePublicKey.Length == 0)
        {
            State.Bridge = info;            // trust on first use; the owner compares the pairing code
        }
        else if (info.BridgePublicKey != State.Bridge.BridgePublicKey)
        {
            byte[] msg = Encoding.UTF8.GetBytes("co.bridge.key.v1\n" + info.BridgePublicKey);
            if (info.PreviousKeySignature == null || !KeyUtil.Verify(State.Bridge.BridgePublicKey, msg, info.PreviousKeySignature))
            {
                StatusChanged?.Invoke("bridge.json carries an unsigned Bridge key change; ignored");
                return State.Bridge;
            }
            State.Bridge = info;
        }
        else
        {
            // Same key: only state and service account fields may change.
            State.Bridge.State = info.State;
            State.Bridge.ServiceAccount = info.ServiceAccount;
            State.Bridge.ServiceAccountKeyId = info.ServiceAccountKeyId;
            State.Bridge.HostId = info.HostId;
            State.Bridge.ProjectId = info.ProjectId;
            State.Bridge.Trusted = info.Trusted;
            State.Bridge.TrustUntil = info.TrustUntil;
            State.Bridge.TrustSig = info.TrustSig;
        }
        Save();
        return State.Bridge;
    }

    public async Task PublishCatalogAsync(CancellationToken ct)
    {
        if (State.PairingRequestFileId.Length == 0) return;
        byte[] req = Encoding.UTF8.GetBytes(Wire.Serialize(BuildRequest()));
        await _drive.UpdateFileAsync(State.PairingRequestFileId, req, "application/json", ct);
    }

    public string? PairingCode() =>
        State.Bridge?.BridgePublicKey is { Length: > 0 } k ? KeyUtil.PairingCode(_keys.SigningKey().PublicKey, k) : null;

    // 0.9.8: the owner granted auto-approval to this device's environment (signed by the pinned Bridge key).
    public bool OwnerTrusted
    {
        get
        {
            var b = State.Bridge;
            if (b is not { Trusted: true } || string.IsNullOrEmpty(b.TrustSig) || string.IsNullOrEmpty(b.TrustUntil) || b.BridgePublicKey.Length == 0)
                return false;
            if (!DateTime.TryParse(b.TrustUntil, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var until)
                || until <= DateTime.UtcNow || until > DateTime.UtcNow.AddDays(3))
                return false;
            try { return KeyUtil.Verify(b.BridgePublicKey, Wire.Payload("co.device.trust.v2", State.DeviceId, "1", b.TrustUntil), b.TrustSig); }
            catch (Exception) { return false; }
        }
    }

    public async Task StartAsync(ILargeResultSink? sink, CancellationToken ct)
    {
        if (!State.Paired || State.Folder == null) throw new InvalidOperationException("Device is not paired.");
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var id = new DeviceIdentity
        {
            DeviceId = State.DeviceId, Signer = _keys.SigningKey(),
            PinnedBridgeKey = State.Bridge!.BridgePublicKey, Managed = State.Managed,
        };
        Host = new ToolHost(id, Catalog, Folders, Snapshots, Grants, _approvals, Audit, sink,
            replayFile: Path.Combine(_dataDir, "replay.tsv"), trusted: () => State.Trusted || OwnerTrusted);

        var sa = new ServiceAccountCredential(new ServiceAccountCredential.Initializer(State.Bridge.ServiceAccount)
        {
            Key = _keys.ServiceAccountKey(),
            KeyId = State.Bridge.ServiceAccountKeyId,
            ProjectId = State.Bridge.ProjectId,
        });
        PubSub = new PubSubChannel(State.Bridge.ProjectId, State.Bridge.HostId, State.DeviceId, GoogleCredential.FromServiceAccountCredential(sa), Version, PubSubRest);
        PubSub.Trace += m => StatusChanged?.Invoke(m);
        PubSub.Cancel = (id, sig) => Host.Cancel(id, sig);
        DriveChannel = new DriveMailboxChannel(_drive, State.Folder, () => PubSub.State == ChannelState.Up) { Cancel = (id, sig) => Host.Cancel(id, sig) };

        // Bridge serves the catalog from the pairing request; an update with new tools reaches it this way (TZ §7.4).
        try { await PublishCatalogAsync(_cts.Token); }
        catch (Exception ex) { StatusChanged?.Invoke("Tool catalog not published: " + ex.Message); }

        // 0.9.8: tell Bridge at once that the call arrived (either channel), so it neither duplicates it via Drive
        // nor gives up on it; the result follows when the tool finishes.
        CallHandler handler = (call, token) =>
        {
            if (PubSub.State != ChannelState.Off)
                _ = PubSub.PublishReceivedAsync(call.CallId);
            return Host.HandleAsync(call, token);
        };
        _channels.Add(DriveChannel);
        await DriveChannel.StartAsync(handler, _cts.Token);
        if (!UsePubSub) StatusChanged?.Invoke("Pub/Sub off for this run, Drive mailbox only");
        else
        {
            try
            {
                _channels.Add(PubSub);
                await PubSub.StartAsync(handler, _cts.Token);
            }
            catch (Exception ex)
            {
                StatusChanged?.Invoke("Pub/Sub unavailable, Drive fallback active: " + ex.Message);
            }
        }
        if (AutoUpdate)
        {
            try { await EnsureUpdateFilesAsync(_cts.Token); }
            catch (Exception ex) { StatusChanged?.Invoke("Update files not created: " + ex.Message); }
        }
        _ = Task.Run(() => DriveHeartbeatLoop(_cts.Token));
        if (AutoUpdate) _ = Task.Run(() => UpdateLoop(_cts.Token));
        StatusChanged?.Invoke("running");
    }

    private async Task DriveHeartbeatLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var hb = new Heartbeat { DeviceId = State.DeviceId, Utc = Wire.Now(DateTime.UtcNow), Version = Version, Channel = "drive" }.WithConnector();
                await _drive.UpdateFileAsync(State.HeartbeatFileId, Encoding.UTF8.GetBytes(Wire.Serialize(hb)), "application/json", ct);
                await RefreshBridgeInfoAsync(ct);
                if (State.Bridge?.State == "revoked") { StatusChanged?.Invoke("device revoked by the owner"); _cts?.Cancel(); }
            }
            catch (Exception) when (!ct.IsCancellationRequested) { }
            try { await Task.Delay(TimeSpan.FromMinutes(5), ct); } catch (OperationCanceledException) { }
        }
    }

    private async Task EnsureUpdateFilesAsync(CancellationToken ct)
    {
        if (State.Folder == null) return;
        if (State.UpdateManifestFileId.Length == 0 || State.UpdateBinFileId.Length == 0)
        {
            // Reuse files created earlier (device.json may have been rewritten); Bridge fills the first one it finds.
            var existing = await _drive.ListChildrenAsync(State.Folder.FolderId, ct);
            if (State.UpdateManifestFileId.Length == 0)
                State.UpdateManifestFileId = existing.FirstOrDefault(f => f.Name == "update.json")?.Id ?? "";
            if (State.UpdateBinFileId.Length == 0)
                State.UpdateBinFileId = existing.FirstOrDefault(f => f.Name == "update.bin")?.Id ?? "";
        }
        if (State.UpdateManifestFileId.Length == 0)
            State.UpdateManifestFileId = await _drive.CreateFileAsync("update.json", State.Folder.FolderId, "{}"u8.ToArray(), "application/json", ct);
        if (State.UpdateBinFileId.Length == 0)
            State.UpdateBinFileId = await _drive.CreateFileAsync("update.bin", State.Folder.FolderId, Array.Empty<byte>(), "application/octet-stream", ct);
        Save();
    }

    private readonly SemaphoreSlim _updateGate = new(1, 1);

    public async Task CheckForUpdateAsync(CancellationToken ct)
    {
        if (!await _updateGate.WaitAsync(0, ct)) return;
        try
        {
            var ready = await Updater.CheckAsync(State, Version, ct);
            if (ready != null) UpdateReady?.Invoke(ready);
        }
        finally { _updateGate.Release(); }
    }

    private async Task UpdateLoop(CancellationToken ct)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(1), ct); } catch (OperationCanceledException) { return; }
        while (!ct.IsCancellationRequested)
        {
            try { await CheckForUpdateAsync(ct); }
            catch (Exception ex) when (!ct.IsCancellationRequested) { StatusChanged?.Invoke("Update check failed: " + ex.Message); }
            try { await Task.Delay(TimeSpan.FromMinutes(30), ct); } catch (OperationCanceledException) { return; }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        foreach (var c in _channels) { try { await c.DisposeAsync(); } catch (Exception) { } }
        _channels.Clear();
    }
}

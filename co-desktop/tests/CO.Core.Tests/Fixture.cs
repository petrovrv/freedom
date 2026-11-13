// SPDX-License-Identifier: AGPL-3.0-or-later
using CO.Core.Crypto;
using CO.Core.Files;
using CO.Core.Host;
using CO.Core.Policy;
using CO.Core.Protocol;
using CO.Core.Tools;

namespace CO.Core.Tests;

public sealed class ScriptedApprovals : IApprovalService
{
    public Queue<ApprovalAnswer> Answers { get; } = new();
    public List<ApprovalRequest> Seen { get; } = new();

    public Task<ApprovalAnswer> RequestAsync(ApprovalRequest request, CancellationToken ct)
    {
        Seen.Add(request);
        return Task.FromResult(Answers.Count > 0 ? Answers.Dequeue() : new ApprovalAnswer(ApprovalChoice.Deny, "no scripted answer"));
    }
}

public sealed class Fixture : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "co-test-" + Guid.NewGuid().ToString("N"));
    public string Work => Path.Combine(Root, "work");
    public string Synced => Path.Combine(Root, "synced");
    public string Mail => Path.Combine(Root, "mail");
    public EcdsaSigner Bridge { get; } = EcdsaSigner.CreateEphemeral();
    public EcdsaSigner Device { get; } = EcdsaSigner.CreateEphemeral();
    public ScriptedApprovals Approvals { get; } = new();
    public FolderRegistry Folders { get; }
    public GrantStore Grants { get; } = new();
    public SnapshotStore Snapshots { get; }
    public AuditLog Audit { get; }
    public ToolHost Host { get; }
    public ToolCatalog Catalog { get; } = new();
    public DateTime Now { get; set; } = new(2026, 9, 26, 12, 0, 0, DateTimeKind.Utc);
    private int _n;

    public Fixture(bool managed = false, int inlineTokens = 20_000, CO.Core.Browser.IBrowser? browser = null, IEnumerable<IDeviceTool>? extraTools = null)
    {
        Directory.CreateDirectory(Work);
        Directory.CreateDirectory(Synced);
        Directory.CreateDirectory(Mail);
        Folders = new FolderRegistry();
        Folders.Add(Work, "work", writable: true, project: true, synced: false);
        Folders.Add(Synced, "od", writable: true, project: false, synced: true);
        Folders.Add(Mail, "inbox", writable: false, project: false, synced: false);
        Snapshots = new SnapshotStore(Path.Combine(Root, "snap"));
        Audit = new AuditLog(Path.Combine(Root, "audit.jsonl"));
        var cat = Catalog;
        foreach (var t in FsTools.All()) cat.Add(t);
        if (browser != null) foreach (var t in BrowserTools.All(browser)) cat.Add(t);
        if (extraTools != null) foreach (var t in extraTools) cat.Add(t);
        Host = new ToolHost(
            new DeviceIdentity { DeviceId = "d-test01", Signer = Device, PinnedBridgeKey = Bridge.PublicKey, Managed = managed },
            cat, Folders, Snapshots, Grants, Approvals, Audit, null,
            new ToolHostOptions { Clock = () => Now, InlineTokenLimit = inlineTokens, PreviewTokens = 50 });
    }

    public ToolCall Call(string tool, object args, string run = "run1", bool tainted = false, ISigner? signer = null)
    {
        var c = new ToolCall
        {
            CallId = "c" + Interlocked.Increment(ref _n), RunId = run, TaskId = run, DeviceId = "d-test01",
            Tool = tool, ArgsJson = System.Text.Json.JsonSerializer.Serialize(args), IssuedUtc = Wire.Now(Now), Tainted = tainted,
        };
        KeyUtil.SignCall(c, signer ?? Bridge);
        return c;
    }

    public Task<ToolResult> Run(string tool, object args, string run = "run1", bool tainted = false) =>
        Host.HandleAsync(Call(tool, args, run, tainted), CancellationToken.None);

    public void Dispose()
    {
        try { Directory.Delete(Root, true); } catch (IOException) { }
    }
}

// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using System.Text.Json.Serialization;
using CO.Core.Protocol;

namespace CO.Core.Transport;

public sealed class DeviceFolderIds
{
    [JsonPropertyName("folderId")] public string FolderId { get; set; } = "";
    [JsonPropertyName("callsId")] public string CallsId { get; set; } = "";
    [JsonPropertyName("resultsId")] public string ResultsId { get; set; } = "";
    [JsonPropertyName("auditId")] public string AuditId { get; set; } = "";
    [JsonPropertyName("slotIds")] public List<string> SlotIds { get; set; } = new();
}

// TZ §4.2 / §6.2.3: every file of the fallback channel is created by Runtime (drive.file).
public static class DeviceFolder
{
    public static async Task<DeviceFolderIds> CreateAsync(IDriveFiles drive, string deviceId, string? shareWithOwner, CancellationToken ct)
    {
        var ids = new DeviceFolderIds { FolderId = await drive.CreateFolderAsync(Names.DeviceFolder(deviceId), null, ct) };
        ids.CallsId = await drive.CreateFolderAsync("calls", ids.FolderId, ct);
        ids.ResultsId = await drive.CreateFolderAsync("results", ids.FolderId, ct);
        ids.AuditId = await drive.CreateFolderAsync("audit", ids.FolderId, ct);
        byte[] empty = new SlotContent().ToBytes();
        for (int i = 0; i < Names.SlotCount; i++)
            ids.SlotIds.Add(await drive.CreateFileAsync(Names.SlotName(i), ids.CallsId, empty, "application/json", ct));
        if (!string.IsNullOrEmpty(shareWithOwner))
            await drive.ShareAsync(ids.FolderId, shareWithOwner, "writer", ct);
        return ids;
    }
}

public sealed class DriveMailboxChannel : IDeviceChannel
{
    private readonly IDriveFiles _drive;
    private readonly DeviceFolderIds _ids;
    private readonly Func<bool> _fastChannelUp;
    private readonly HashSet<string> _handled = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTime> _slotSeen = new(StringComparer.Ordinal);
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public TimeSpan FastInterval { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan IdleInterval { get; init; } = TimeSpan.FromSeconds(60);

    public DriveMailboxChannel(IDriveFiles drive, DeviceFolderIds ids, Func<bool> fastChannelUp)
    {
        _drive = drive;
        _ids = ids;
        _fastChannelUp = fastChannelUp;
    }

    public string Name => "drive";
    // 0.9.8: signed cancel from Bridge (callId, signature); set by DeviceRuntime.
    public Func<string, string, bool>? Cancel { get; set; }
    public ChannelState State { get; private set; } = ChannelState.Off;

    public Task StartAsync(CallHandler handler, CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        State = ChannelState.Starting;
        _loop = Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    await PollOnceAsync(handler, _cts.Token);
                    State = ChannelState.Up;
                }
                catch (OperationCanceledException) when (_cts.IsCancellationRequested) { break; }
                catch (Exception) { State = ChannelState.Degraded; }
                // TZ §4.2.4: 2 s while the Pub/Sub channel is down, otherwise an idle check.
                try { await Task.Delay(_fastChannelUp() ? IdleInterval : FastInterval, _cts.Token); }
                catch (OperationCanceledException) { break; }
            }
        });
        return Task.CompletedTask;
    }

    public async Task PollOnceAsync(CallHandler handler, CancellationToken ct)
    {
        var slots = await _drive.ListChildrenAsync(_ids.CallsId, ct);
        foreach (DriveFile slot in slots.Where(s => _ids.SlotIds.Contains(s.Id)))
        {
            if (_slotSeen.TryGetValue(slot.Id, out var seen) && seen >= slot.ModifiedUtc && slot.ModifiedUtc != default) continue;
            SlotContent c = SlotContent.Parse(await _drive.DownloadAsync(slot.Id, ct));
            _slotSeen[slot.Id] = slot.ModifiedUtc;
            if (c.State == "cancelled" && c.DoneCallId != null && c.CancelSig != null)
            {
                Cancel?.Invoke(c.DoneCallId, c.CancelSig);
                continue;
            }
            if (c.State != "call" || c.Call == null || _handled.Contains(c.Call.CallId)) continue;
            _handled.Add(c.Call.CallId);

            ToolResult r = await handler(c.Call, ct);
            await _drive.CreateFileAsync(r.CallId + ".json", _ids.ResultsId, Encoding.UTF8.GetBytes(Wire.Serialize(r)), "application/json", ct);
            // Bridge may have reused the slot meanwhile; mark it done only if it still holds this call.
            SlotContent now = SlotContent.Parse(await _drive.DownloadAsync(slot.Id, ct));
            if (now.State == "call" && now.Call?.CallId == r.CallId)
                await _drive.UpdateFileAsync(slot.Id, new SlotContent { State = "done", DoneCallId = r.CallId }.ToBytes(), "application/json", ct);
            _slotSeen.Remove(slot.Id); // our own update changes modifiedTime; re-read next round
        }

        // Results read by Bridge carry appProperties.consumed = "true" (TZ §4.2.3).
        foreach (DriveFile f in await _drive.ListChildrenAsync(_ids.ResultsId, ct))
            if (f.AppProperties.TryGetValue("consumed", out var v) && v == "true")
                await _drive.DeleteAsync(f.Id, ct);

        if (_handled.Count > 10_000) _handled.Clear(); // ReplayGuard in ToolHost is the real duplicate barrier
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        if (_loop != null) { try { await _loop; } catch (OperationCanceledException) { } }
        State = ChannelState.Off;
    }
}

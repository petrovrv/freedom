// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CO.Core.Protocol;
using Google.Api.Gax.Grpc;
using Google.Apis.Auth.OAuth2;
using Google.Cloud.PubSub.V1;
using Google.Protobuf;
using Grpc.Auth;

namespace CO.Core.Transport;

// TZ §4.1: device side of the fast channel. gRPC streaming pull on the device's call subscription,
// results and heartbeats published to co-<host>-dev-results with the device service account.
public sealed class PubSubChannel : IDeviceChannel
{
    private readonly string _project, _host, _deviceId, _version;
    private readonly GoogleCredential _credential;
    private SubscriberClient? _subscriber;
    private PublisherClient? _publisher;
    private Task? _run, _beat;
    private CancellationTokenSource? _cts;
    private int _active;
    private volatile bool _subscribed;   // 0.9.8: the calls subscription is being pulled right now
    // co-resident 0.3.3: REST (HTTP/1.1) instead of gRPC on the Mac. On 09.11 a member's Mac (macOS 15.6) aborted within seconds of every start
    // with an AccessViolationException in SslStream under Grpc.Net.Client (HTTP/2), and the resident fell back to the
    // Drive mailbox; Drive runs over the same HTTP/1.1 stack there without trouble.
    private readonly bool _rest;
    private HttpClient? _http;
    private const string Api = "https://pubsub.googleapis.com/v1/";

    public PubSubChannel(string project, string host, string deviceId, GoogleCredential credential, string version, bool rest = false)
    {
        _project = project;
        _host = host;
        _deviceId = deviceId;
        _credential = credential.CreateScoped("https://www.googleapis.com/auth/pubsub");
        _version = version;
        _rest = rest;
    }

    public string Name => "pubsub";
    public ChannelState State { get; private set; } = ChannelState.Off;
    public DateTime LastSuccessUtc { get; private set; }
    public string LastError { get; private set; } = "";
    // 0.9.8: signed cancel from Bridge (callId, signature); set by DeviceRuntime.
    public Func<string, string, bool>? Cancel { get; set; }
    public event Action<string>? Trace;

    public async Task StartAsync(CallHandler handler, CancellationToken ct)
    {
        State = ChannelState.Starting;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (_rest)
        {
            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
            _run = Task.Run(() => RestPullLoop(handler, _cts.Token));
            _beat = Task.Run(() => HeartbeatLoop(_cts.Token));
            return;
        }
        var channelCredentials = _credential.ToChannelCredentials();

        _publisher = await new PublisherClientBuilder
        {
            TopicName = TopicName.FromProjectTopic(_project, Names.ResultsTopic(_host)),
            ChannelCredentials = channelCredentials,
            Settings = new PublisherClient.Settings { BatchingSettings = new Google.Api.Gax.BatchingSettings(1, null, TimeSpan.FromMilliseconds(1)) },
        }.BuildAsync(ct);

        _run = Task.Run(() => SubscribeLoop(handler, channelCredentials, _cts.Token));
        _beat = Task.Run(() => HeartbeatLoop(_cts.Token));
    }

    // 0.9.8: the streaming pull ends for good on a non-retryable error (on 08.11 after the computer clock had been
    // wrong, the service-account token was refused); earlier versions never restarted it, so the device kept sending
    // heartbeats but no longer got calls. Now the pull is rebuilt with a growing pause (5 s .. 2 min).
    private async Task SubscribeLoop(CallHandler handler, Grpc.Core.ChannelCredentials channelCredentials, CancellationToken ct)
    {
        var pause = TimeSpan.FromSeconds(5);
        while (!ct.IsCancellationRequested)
        {
            DateTime started = DateTime.UtcNow;
            try
            {
                _subscriber = await new SubscriberClientBuilder
                {
                    SubscriptionName = SubscriptionName.FromProjectSubscription(_project, Names.CallsSubscription(_host, _deviceId)),
                    ChannelCredentials = channelCredentials,
                    // One streaming pull is plenty for a device; the default opens one gRPC channel (and TLS handshake) per CPU core.
                    ClientCount = 1,
                    Settings = new SubscriberClient.Settings
                    {
                        FlowControlSettings = new Google.Api.Gax.FlowControlSettings(16, null),
                        AckDeadline = TimeSpan.FromSeconds(60),
                    },
                }.BuildAsync(ct);
                _subscribed = true;
                State = ChannelState.Up;
                await SubscribeOnce(_subscriber, handler);
                if (ct.IsCancellationRequested) break;
                LastError = "calls subscription stopped";
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                LastError = ex.Message;
            }
            finally { _subscribed = false; }
            if (ct.IsCancellationRequested) break;
            State = ChannelState.Down;
            if (DateTime.UtcNow - started > TimeSpan.FromMinutes(10)) pause = TimeSpan.FromSeconds(5);
            Trace?.Invoke($"Pub/Sub calls subscription ended ({LastError}); retry in {(int)pause.TotalSeconds} s, Drive fallback meanwhile");
            try { await Task.Delay(pause, ct); } catch (OperationCanceledException) { break; }
            pause = TimeSpan.FromSeconds(Math.Min(120, pause.TotalSeconds * 2));
        }
    }

    private Task SubscribeOnce(SubscriberClient subscriber, CallHandler handler) =>
        subscriber.StartAsync((msg, token) =>
        {
            msg.Attributes.TryGetValue("type", out var kind);
            Dispatch(kind, msg.Data.ToStringUtf8(), handler);
            return Task.FromResult(SubscriberClient.Reply.Ack);
        });

    // One delivered message, acked first: the call is executed at most once; ReplayGuard covers redelivery anyway.
    private void Dispatch(string? kind, string data, CallHandler handler)
    {
        State = ChannelState.Up;
        LastSuccessUtc = DateTime.UtcNow;
        if (kind == "tool.cancel")
        {
            try
            {
                using var doc = JsonDocument.Parse(data);
                var root = doc.RootElement;
                if (root.TryGetProperty("callId", out var id) && root.TryGetProperty("sig", out var sg))
                    Cancel?.Invoke(id.GetString() ?? "", sg.GetString() ?? "");
            }
            catch (Exception) { }
            return;
        }
        ToolCall call;
        try { call = Wire.Deserialize<ToolCall>(data); }
        catch (Exception) { return; } // malformed: drop (the audit only covers calls)

        _ = Task.Run(async () =>
        {
            Interlocked.Increment(ref _active);
            try
            {
                ToolResult r = await handler(call, _cts!.Token);
                await PublishAsync("tool.result", call.CallId, Wire.Serialize(r));
            }
            finally { Interlocked.Decrement(ref _active); }
        });
    }

    // co-resident 0.3.3: synchronous pull over REST; the server holds an empty pull for a while, so this is a long poll.
    private async Task RestPullLoop(CallHandler handler, CancellationToken ct)
    {
        var pause = TimeSpan.FromSeconds(5);
        string sub = $"projects/{_project}/subscriptions/{Names.CallsSubscription(_host, _deviceId)}";
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var acks = new List<string>();
                var msgs = new List<(string? Kind, string Data)>();
                using (var doc = await RestAsync(sub + ":pull", new { maxMessages = 16 }, ct))
                {
                    if (doc.RootElement.TryGetProperty("receivedMessages", out var list))
                        foreach (var rm in list.EnumerateArray())
                        {
                            acks.Add(rm.GetProperty("ackId").GetString() ?? "");
                            var m = rm.GetProperty("message");
                            string data = m.TryGetProperty("data", out var d) ? System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(d.GetString() ?? "")) : "";
                            string? kind = m.TryGetProperty("attributes", out var a) && a.TryGetProperty("type", out var t) ? t.GetString() : null;
                            msgs.Add((kind, data));
                        }
                }
                _subscribed = true;
                State = ChannelState.Up;
                LastSuccessUtc = DateTime.UtcNow;
                pause = TimeSpan.FromSeconds(5);
                if (acks.Count > 0) (await RestAsync(sub + ":acknowledge", new { ackIds = acks }, ct)).Dispose();
                foreach (var (kind, data) in msgs) Dispatch(kind, data, handler);
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                // an empty pull held past the HTTP timeout
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _subscribed = false;
                LastError = ex.Message;
                State = ChannelState.Down;
                Trace?.Invoke($"Pub/Sub pull failed ({LastError}); retry in {(int)pause.TotalSeconds} s, Drive fallback meanwhile");
                try { await Task.Delay(pause, ct); } catch (OperationCanceledException) { break; }
                pause = TimeSpan.FromSeconds(Math.Min(120, pause.TotalSeconds * 2));
            }
        }
        _subscribed = false;
    }

    private async Task<JsonDocument> RestAsync(string path, object body, CancellationToken ct)
    {
        string token = await ((ITokenAccess)_credential).GetAccessTokenForRequestAsync(Api, ct);
        using var req = new HttpRequestMessage(HttpMethod.Post, Api + path)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var resp = await _http!.SendAsync(req, ct);
        string text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"Pub/Sub {(int)resp.StatusCode}: {(text.Length > 300 ? text[..300] : text)}");
        return JsonDocument.Parse(text.Length == 0 ? "{}" : text);
    }

    // 0.9.8: "I have the call" for Bridge (attributes only); errors are ignored, Bridge then waits or uses Drive.
    public async Task PublishReceivedAsync(string callId)
    {
        try { if (_publisher != null || _http != null) await PublishAsync("tool.received", callId, "{}"); }
        catch (Exception) { }
    }

    public Task PublishAsync(string type, string callId, string json)
    {
        if (_rest) return RestPublishAsync(type, callId, json);
        var m = new PubsubMessage { Data = ByteString.CopyFromUtf8(json) };
        m.Attributes["type"] = type;
        m.Attributes["deviceId"] = _deviceId;
        if (callId.Length > 0) m.Attributes["callId"] = callId;
        return _publisher!.PublishAsync(m);
    }

    private async Task HeartbeatLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var hb = new Heartbeat
                {
                    DeviceId = _deviceId, Utc = Wire.Now(DateTime.UtcNow), Version = _version,
                    Channel = Name, ActiveCalls = Volatile.Read(ref _active),
                }.WithConnector(); // 0.7.13 session, 0.8.2 number, 0.11.1 corp
                await PublishAsync("device.heartbeat", "", Wire.Serialize(hb));
                LastSuccessUtc = DateTime.UtcNow;
                // 0.9.8: up only while calls are pulled too; otherwise the Drive mailbox polls every 2 s.
                State = _subscribed ? ChannelState.Up : ChannelState.Degraded;
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                State = DateTime.UtcNow - LastSuccessUtc > TimeSpan.FromSeconds(90) ? ChannelState.Down : ChannelState.Degraded;
            }
            try { await Task.Delay(TimeSpan.FromSeconds(30), ct); } catch (OperationCanceledException) { }
        }
    }

    private async Task RestPublishAsync(string type, string callId, string json)
    {
        var attrs = new Dictionary<string, string> { ["type"] = type, ["deviceId"] = _deviceId };
        if (callId.Length > 0) attrs["callId"] = callId;
        var msg = new { data = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json)), attributes = attrs };
        string topic = $"projects/{_project}/topics/{Names.ResultsTopic(_host)}";
        using var doc = await RestAsync(topic + ":publish", new { messages = new[] { msg } }, _cts?.Token ?? CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        if (_subscriber != null) await _subscriber.StopAsync(new SubscriberClient.ShutdownOptions { Timeout = TimeSpan.FromSeconds(5) }, CancellationToken.None);
        if (_publisher != null) await _publisher.ShutdownAsync(TimeSpan.FromSeconds(5));
        _http?.Dispose();
        State = ChannelState.Off;
    }
}

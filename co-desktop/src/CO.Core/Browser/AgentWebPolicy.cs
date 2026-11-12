// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace CO.Core.Browser;

// Persisted settings for the agent browser (CO Desktop 0.8.0).
// 0.9.0: blocklist instead of allowlist. Every public site is reachable except the domains in Block (a domain covers
// its subdomains); local and private addresses stay blocked always. The 0.8.x allowlist "Allow" is no longer read: on
// the first load it is moved to AllowLegacy (kept for history, never applied). browser-agent 0.7.0 reads the same file.
public sealed class AgentWebSettings
{
    public List<string> Block { get; set; } = new();
    public bool Journal { get; set; } = true;
    // Opt-in: when true, clicks on elements of category "click" (unclear effect) ask the user first.
    // Default false: ordinary buttons and links are clicked without confirmation.
    // "send" stays refused and "purchase"/"delete"/"submit" stay confirmed regardless of this flag.
    public bool ConfirmClicks { get; set; }
    // Default true: form submits (button or Enter), select changes and file uploads ask the user first.
    // When false (CO Desktop 0.8.9) they run without asking; "purchase"/"delete" still ask and "send" stays refused.
    public bool ConfirmForms { get; set; } = true;
    // 0.9.0: the former allowlist, kept unused; null = none (not written).
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? AllowLegacy { get; set; }
}

// JSON-backed store; readers always get a defensive copy.
public sealed class AgentWebSettingsStore
{
    static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
    readonly string _file;
    readonly object _lock = new();
    AgentWebSettings _current;

    public event Action? Changed;

    public AgentWebSettingsStore(string file)
    {
        _file = file;
        _current = Load(file, out bool migrate);
        // 0.9.0: rewrite a 0.8.x file once, so "Allow" becomes "AllowLegacy" on disk too.
        if (migrate)
        {
            try { lock (_lock) Write(_current); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* the next save retries */ }
        }
    }

    public AgentWebSettings Current { get { lock (_lock) return Copy(_current); } }

    // 0.9.0: AllowLegacy is kept when the caller (the UI) does not pass it.
    public void Save(AgentWebSettings s)
    {
        lock (_lock)
        {
            var legacy = s.AllowLegacy ?? _current.AllowLegacy;
            var n = new AgentWebSettings
            {
                Journal = s.Journal,
                ConfirmClicks = s.ConfirmClicks,
                ConfirmForms = s.ConfirmForms,
                Block = Normalize(s.Block),
                AllowLegacy = legacy is null ? null : new List<string>(legacy),
            };
            Write(n);
            _current = n;
        }
        Changed?.Invoke();
    }

    // Under _lock: atomic write (tmp + rename); browser-agent reads the file too.
    void Write(AgentWebSettings n)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(_file));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = _file + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(n, JsonOpts));
        File.Move(tmp, _file, overwrite: true);
    }

    static AgentWebSettings Load(string file, out bool migrate)
    {
        migrate = false;
        try
        {
            if (!File.Exists(file)) return new();
            if (JsonNode.Parse(File.ReadAllText(file)) is not JsonObject root) return new();
            var s = new AgentWebSettings
            {
                Journal = Bool(root, "Journal") ?? true,
                ConfirmClicks = Bool(root, "ConfirmClicks") ?? false,
                ConfirmForms = Bool(root, "ConfirmForms") ?? true,
                Block = Normalize(Strings(root, "Block")),
            };
            var legacy = Strings(root, "AllowLegacy");
            // 0.9.0: a 0.8.x allowlist moves to AllowLegacy (an AllowLegacy already present wins); it is never applied.
            if (root.ContainsKey("Allow"))
            {
                migrate = true;
                if (legacy is null || legacy.Count == 0) legacy = Strings(root, "Allow") ?? new List<string?>();
            }
            s.AllowLegacy = legacy is null ? null : Normalize(legacy);
            return s;
        }
        catch { migrate = false; return new(); } // unreadable or corrupt -> defaults
    }

    static bool? Bool(JsonObject o, string name) =>
        o[name] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;

    static List<string?>? Strings(JsonObject o, string name)
    {
        if (o[name] is not JsonArray a) return null;
        var list = new List<string?>();
        foreach (var n in a)
            if (n is JsonValue v && v.TryGetValue<string>(out var str)) list.Add(str);
        return list;
    }

    static AgentWebSettings Copy(AgentWebSettings c) => new()
    {
        Journal = c.Journal,
        ConfirmClicks = c.ConfirmClicks,
        ConfirmForms = c.ConfirmForms,
        Block = new List<string>(c.Block),
        AllowLegacy = c.AllowLegacy is null ? null : new List<string>(c.AllowLegacy),
    };

    // Reduce each entry to a bare lowercase host; drop empties and duplicates.
    public static List<string> Normalize(IEnumerable<string?>? items)
    {
        var result = new List<string>();
        foreach (var raw in items ?? Enumerable.Empty<string?>())
        {
            var h = (raw ?? "").Trim().ToLowerInvariant();
            if (h.StartsWith("https://")) h = h[8..];
            else if (h.StartsWith("http://")) h = h[7..];
            var cut = h.IndexOfAny(new[] { '/', '?', '#' });
            if (cut >= 0) h = h[..cut];
            if (h.StartsWith("*.")) h = h[2..];
            h = h.Trim().TrimEnd('.');
            if (h.Length > 0 && !result.Contains(h)) result.Add(h);
        }
        return result;
    }
}

public static class AgentWebGuard
{
    // 0.9.0: reason for documents and subresources of a domain on the blocked list.
    public const string BlockedListReason = "domain is on the blocked sites list";
    public const string PrivateReason = "private or local address";

    // Local names and private/loopback/link-local literals; no DNS resolution.
    public static bool IsAlwaysBlockedHost(string host)
    {
        var h = (host ?? "").Trim().TrimEnd('.').ToLowerInvariant();
        if (h.StartsWith('[') && h.EndsWith(']')) h = h[1..^1];
        if (h.Length == 0 || h == "localhost" || h.EndsWith(".localhost") || h.EndsWith(".local")) return true;
        return IPAddress.TryParse(h, out var ip) && IsBlockedIp(ip);
    }

    public static bool IsBlockedIp(IPAddress ip)
    {
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (ip.IsIPv4MappedToIPv6) return IsBlockedIp(ip.MapToIPv4());
            var b6 = ip.GetAddressBytes();
            if (b6.Take(15).All(x => x == 0) && b6[15] <= 1) return true;        // :: and ::1
            return (b6[0] & 0xFE) == 0xFC                                        // fc00::/7
                || (b6[0] == 0xFE && (b6[1] & 0xC0) == 0x80);                    // fe80::/10
        }
        var b = ip.GetAddressBytes();
        return b[0] == 0 || b[0] == 10 || b[0] == 127
            || (b[0] == 169 && b[1] == 254)
            || (b[0] == 172 && (b[1] & 0xF0) == 16)
            || (b[0] == 192 && b[1] == 168);
    }

    // 0.9.0 (was HostAllowed): "example.com" matches itself and its subdomains; "*" matches anything.
    public static bool HostBlocked(string host, IEnumerable<string>? block)
    {
        var h = (host ?? "").Trim().Trim('[', ']').TrimEnd('.').ToLowerInvariant();
        if (h.Length == 0 || block is null) return false;
        foreach (var raw in block)
        {
            var a = (raw ?? "").Trim().TrimEnd('.').ToLowerInvariant();
            if (a.StartsWith("*.")) a = a[2..];
            if (a == "*") return true;
            if (a.Length > 0 && (h == a || h.EndsWith("." + a))) return true;
        }
        return false;
    }

    // Top-level navigation (and frame document) check; null means allowed.
    // 0.9.0: any public http(s) site except the blocked list.
    public static string? CheckDocumentUrl(string url, IEnumerable<string>? block)
    {
        if (string.Equals(url?.Trim(), "about:blank", StringComparison.OrdinalIgnoreCase)) return null;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps))
            return "scheme not allowed";
        if (IsAlwaysBlockedHost(u.Host)) return PrivateReason;
        return HostBlocked(u.Host, block) ? BlockedListReason : null;
    }

    // Short-lived cache of DNS lookups: host name -> whether any resolved address is private/local, and expiry time (UTC).
    static readonly ConcurrentDictionary<string, (bool Private, DateTime Until)> ResolveCache = new(StringComparer.OrdinalIgnoreCase);

    // A public-looking name may resolve to a local or private address (for example a wildcard DNS name that points
    // at the loopback). Such names are always blocked, like literal private addresses: a site could otherwise
    // redirect the agent to a local service.
    public static async Task<string?> CheckResolvedAsync(string url, IEnumerable<string>? sites, CancellationToken ct)
    {
        // Only absolute web URLs are relevant; anything else is handled elsewhere.
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) ||
            (u.Scheme != "http" && u.Scheme != "https" && u.Scheme != "ws" && u.Scheme != "wss"))
            return null;

        // IPv6 hosts may come wrapped in brackets.
        var host = u.Host.Trim('[', ']');

        // IP literals are checked elsewhere.
        if (IPAddress.TryParse(host, out _))
            return null;

        bool priv;
        if (ResolveCache.TryGetValue(host, out var cached) && cached.Until > DateTime.UtcNow)
        {
            // Fresh cached result.
            priv = cached.Private;
        }
        else
        {
            try
            {
                var addrs = await Dns.GetHostAddressesAsync(host, ct);
                priv = addrs.Any(IsBlockedIp);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // Resolution failed: nothing to judge here, the request itself will fail or be checked later.
                return null;
            }

            ResolveCache[host] = (priv, DateTime.UtcNow.AddSeconds(5));
        }

        // Name resolves only to public addresses.
        if (!priv)
            return null;

        // The site list is unused on purpose (kept for compatibility): no list ever opens private or local targets.
        _ = sites;
        return "name resolves to a private or local address";
    }

    // Subresource check: network schemes targeting local/private hosts and (0.9.0) blocked domains, so the blocked
    // list cannot be bypassed with fetch/XHR/WebSocket, images or scripts.
    public static string? CheckSubresourceUrl(string url, IEnumerable<string>? block = null)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return null;
        if (u.Scheme is not ("http" or "https" or "ws" or "wss")) return null;
        if (IsAlwaysBlockedHost(u.Host)) return PrivateReason;
        return HostBlocked(u.Host, block) ? BlockedListReason : null;
    }
}

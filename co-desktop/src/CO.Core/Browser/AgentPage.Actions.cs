// SPDX-License-Identifier: AGPL-3.0-or-later
// PART 2: accessibility snapshot with refs and the interactions (click, type, select, press, scroll, upload).

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CO.Core.Tools;

namespace CO.Core.Browser;

public sealed record ActionInfo(string? Category, string Description, IReadOnlyList<FormField> Fields);
public sealed record FormField(string Label, string Type, string Value);
public sealed record Snapshot(string Url, string Title, string Text, bool Truncated);

public sealed partial class AgentPage
{
    // Marks an argument that must be passed to callFunctionOn as a remote object, not a value.
    private sealed record RemoteRef(string ObjectId);

    // Unnamed nodes get no line of their own (children render at the same depth)
    // unless their role is interactive or a landmark/container worth showing.
    private static readonly HashSet<string> InteractiveRoles =
        new(StringComparer.Ordinal)
        {
            "button", "link", "textbox", "searchbox", "combobox", "checkbox", "radio", "switch",
            "slider", "spinbutton", "menuitem", "menuitemcheckbox", "menuitemradio", "tab",
            "option", "listbox", "treeitem", "InputTime", "DateTime", "textarea"
        };

    private static readonly HashSet<string> ShownWhenUnnamed =
        new(StringComparer.Ordinal)
        {
            "form", "dialog", "alertdialog", "navigation", "main", "list", "listitem", "table",
            "row", "cell", "columnheader", "rowheader", "heading", "img", "StaticText"
        };

    // Roles whose current value is shown after the name.
    private static readonly HashSet<string> ValueRoles =
        new(StringComparer.Ordinal) { "textbox", "searchbox", "combobox" };

    private static readonly HashSet<string> StateProps =
        new(StringComparer.Ordinal) { "checked", "pressed", "selected", "expanded" };

    private static readonly Dictionary<string, (string Key, string Code, int Vk, string? Text)> Keys =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Enter"] = ("Enter", "Enter", 13, "\r"),
            ["Tab"] = ("Tab", "Tab", 9, null),
            ["Escape"] = ("Escape", "Escape", 27, null),
            ["Backspace"] = ("Backspace", "Backspace", 8, null),
            ["Delete"] = ("Delete", "Delete", 46, null),
            ["Space"] = (" ", "Space", 32, " "),
            ["ArrowUp"] = ("ArrowUp", "ArrowUp", 38, null),
            ["ArrowDown"] = ("ArrowDown", "ArrowDown", 40, null),
            ["ArrowLeft"] = ("ArrowLeft", "ArrowLeft", 37, null),
            ["ArrowRight"] = ("ArrowRight", "ArrowRight", 39, null),
            ["PageUp"] = ("PageUp", "PageUp", 33, null),
            ["PageDown"] = ("PageDown", "PageDown", 34, null),
            ["Home"] = ("Home", "Home", 36, null),
            ["End"] = ("End", "End", 35, null),
        };

    // After input that may navigate: give the page up to 1.5 s to start a navigation (or get it blocked), then wait
    // until it settles. Without this a slow click handler could return before a blocked navigation is visible.
    private async Task SettleAfterInputAsync(CdpSession s, int navBefore, CancellationToken ct)
    {
        const int maxWaitMs = 1500;
        const int pollMs = 50;
        var waited = 0;
        while (waited < maxWaitMs && Volatile.Read(ref _navSeq) == navBefore && _lastBlock is null)
        {
            await Task.Delay(pollMs, ct);
            waited += pollMs;
        }

        // A navigation started but no block is recorded yet: the request may still be paused, give it a moment.
        if (_lastBlock is null && Volatile.Read(ref _navSeq) != navBefore)
            await Task.Delay(300, ct);

        await WaitSettledAsync(s, ct);
    }

    public static long ParseRef(string r)
    {
        var t = r?.Trim() ?? "";
        if (t.Length > 1 && t[0] == 'n'
            && long.TryParse(t.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0)
            return id;
        throw new ToolException("bad_ref",
            "Unknown ref '" + r + "'. Refs look like n123; take them from the latest browser_snapshot.");
    }

    // A failure reported by the browser (not cancellation and not one of our own tool errors).
    private static bool IsCdpError(Exception e) => e is not OperationCanceledException and not ToolException;

    // The user may remove a site from the list while its page is open: every operation re-checks the open page.
    // Re-checks the pause state inside the queued action, so a call queued before the user
    // pressed Take over does not run afterwards. Snapshot keeps using RequireAllowedPageAsync.
    private async Task RequireActionAllowedAsync(CdpSession s, CancellationToken ct)
    {
        RequireNotPaused();
        await RequireAllowedPageAsync(s, ct);
    }

    private async Task RequireAllowedPageAsync(CdpSession s, CancellationToken ct)
    {
        var url = (await InfoAsync(s, ct)).Url;
        if (string.IsNullOrEmpty(url) || url == "about:blank") return;
        var reason = AgentWebGuard.CheckDocumentUrl(url, _settings.Current.Block);
        if (reason is null) return;
        // Best effort: leave the blocked page before reporting.
        try { await s.SendAsync("Page.navigate", new { url = "about:blank" }, ct); }
        catch (Exception e) when (IsCdpError(e)) { }
        throw Blocked(url, reason);
    }

    // Background tabs are not rendered, so CDP input events would never be acknowledged. The agent works in
    // visible mode: its tab comes to the front of the CO browser before clicks, keys and scrolling.
    // Tries to make the tab visible (bring to front, restore a minimized window).
    // Returns true when document.visibilityState is "visible", false otherwise.
    private static async Task<bool> EnsureVisibleAsync(CdpSession s, CancellationToken ct)
    {
        if (await VisibilityStateAsync(s, ct) == "visible") return true;

        await s.SendAsync("Page.bringToFront", null, ct);

        // Page.bringToFront does not restore a minimized window, so restore it explicitly.
        try
        {
            var win = await s.SendAsync("Browser.getWindowForTarget", null, ct);
            if (win.TryGetProperty("windowId", out var windowIdEl)
                && win.TryGetProperty("bounds", out var boundsEl)
                && boundsEl.TryGetProperty("windowState", out var windowStateEl)
                && windowStateEl.GetString() == "minimized")
            {
                await s.SendAsync("Browser.setWindowBounds",
                    new { windowId = windowIdEl.GetInt32(), bounds = new { windowState = "normal" } }, ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The Browser domain may be unavailable on this session; ignore and rely on polling.
        }

        // Poll every 100 ms for up to 2 s until the tab reports itself as visible.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(100, ct);
            if (await VisibilityStateAsync(s, ct) == "visible") return true;
        }

        // Still hidden after polling: input events will not reach the page.
        return false;

        static async Task<string?> VisibilityStateAsync(CdpSession s, CancellationToken ct)
        {
            var r = await s.SendAsync("Runtime.evaluate", new { expression = "document.visibilityState", returnByValue = true }, ct);
            return r.ValueKind == JsonValueKind.Object && r.TryGetProperty("result", out var res) ? Str(res, "value") : null;
        }
    }

    private static async Task<string> ObjectIdAsync(CdpSession s, long backendNodeId, int contextId, CancellationToken ct)
    {
        var gone = new ToolException("bad_ref", $"The element n{backendNodeId} is gone; take a new browser_snapshot.");
        JsonElement r;
        try
        {
            r = await s.SendAsync("DOM.resolveNode", new { backendNodeId, executionContextId = contextId }, ct);
        }
        catch (Exception e) when (IsCdpError(e)) { throw gone; }

        if (r.TryGetProperty("object", out var o) && Str(o, "objectId") is { Length: > 0 } id) return id;
        throw gone;
    }

    private static async Task ReleaseAsync(CdpSession s, string objectId)
    {
        try { await s.SendAsync("Runtime.releaseObject", new { objectId }, CancellationToken.None); }
        catch { /* best effort: the object dies with the page anyway */ }
    }

    private static async Task<JsonElement> CallAsync(CdpSession s, long node, string fn, CancellationToken ct, params object[] args)
    {
        var ctx = await WorldAsync(s, OpsWorld, ct);
        var objectId = await ObjectIdAsync(s, node, ctx, ct);
        try
        {
            var r = await s.SendAsync("Runtime.callFunctionOn", new
            {
                functionDeclaration = fn,
                objectId,
                arguments = args.Select(a => a is RemoteRef rr ? (object)new { objectId = rr.ObjectId } : new { value = a }).ToArray(),
                returnByValue = true,
                awaitPromise = true,
            }, ct);
            ThrowOnException(r);
            return r.TryGetProperty("result", out var res) && res.TryGetProperty("value", out var v) ? v.Clone() : default;
        }
        finally { await ReleaseAsync(s, objectId); }
    }

    public Task<Snapshot> SnapshotAsync(int maxChars, CancellationToken ct) => RunAsync(async s =>
    {
        // The user may be typing private data while they control the tab: do not read the page.
        RequireNotPaused();
        await RequireAllowedPageAsync(s, ct);
        var r = await s.SendAsync("Accessibility.getFullAXTree", null, ct);
        var nodes = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var roots = new List<string>();
        if (r.TryGetProperty("nodes", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var n in arr.EnumerateArray())
            {
                if (Str(n, "nodeId") is not { } id) continue;
                nodes[id] = n;
                if (!n.TryGetProperty("parentId", out var p) || p.ValueKind != JsonValueKind.String) roots.Add(id);
            }
        }
        if (roots.Count == 0 && nodes.Count > 0) roots.Add(nodes.Keys.First());

        var sb = new StringBuilder();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var truncated = false;
        // Backend node ids of protected inputs (secret, payment, bank, one-time code); their values are never rendered.
        var protectedIds = await ProtectedNodesAsync(s, ct);
        foreach (var root in roots) Render(nodes, root, 0, null, sb, maxChars, protectedIds, seen, ref truncated);
        if (truncated) sb.Append("\n... [truncated]");

        var info = await InfoAsync(s, ct);
        return new Snapshot(info.Url, info.Title, sb.ToString().TrimEnd('\n'), truncated);
    }, ct);

    // CSS selector matching inputs whose values must never appear in a snapshot.
    // The trailing 'i' flag makes attribute matching case-insensitive.
    private const string ProtectedSelector =
        "input[type=\"password\" i]," +
        "input[autocomplete^=\"cc-\" i]," +
        "input[autocomplete=\"one-time-code\" i]," +
        "input[name*=\"cardnum\" i],input[id*=\"cardnum\" i],input[name*=\"card_num\" i],input[id*=\"card_num\" i]," +
        "input[name*=\"card-num\" i],input[id*=\"card-num\" i],input[name*=\"ccnum\" i],input[id*=\"ccnum\" i]," +
        "input[name*=\"cvc\" i],input[id*=\"cvc\" i]," +
        "input[name*=\"cvv\" i],input[id*=\"cvv\" i]," +
        "input[name*=\"iban\" i],input[id*=\"iban\" i]," +
        "input[name*=\"otp\" i],input[id*=\"otp\" i]," +
        "input[name*=\"pin\" i],input[id*=\"pin\" i]," +
        "input[name*=\"password\" i],input[id*=\"password\" i]," +
        "input[name*=\"secret\" i],input[id*=\"secret\" i]," +
        "input[name*=\"token\" i],input[id*=\"token\" i]," +
        "input[aria-label*=\"cvc\" i],input[placeholder*=\"cvc\" i]," +
        "input[aria-label*=\"cvv\" i],input[placeholder*=\"cvv\" i]," +
        "input[aria-label*=\"csc\" i],input[placeholder*=\"csc\" i]," +
        "input[aria-label*=\"security code\" i],input[placeholder*=\"security code\" i]," +
        "input[aria-label*=\"card number\" i],input[placeholder*=\"card number\" i]," +
        "input[aria-label*=\"iban\" i],input[placeholder*=\"iban\" i]," +
        "input[aria-label*=\"otp\" i],input[placeholder*=\"otp\" i]," +
        "input[aria-label*=\"one-time\" i],input[placeholder*=\"one-time\" i]," +
        "input[aria-label*=\"verification\" i],input[placeholder*=\"verification\" i]," +
        "input[aria-label*=\"password\" i],input[placeholder*=\"password\" i]," +
        "input[aria-label*=\"wachtwoord\" i],input[placeholder*=\"wachtwoord\" i]," +
        "input[aria-label*=\"пароль\" i],input[placeholder*=\"пароль\" i]," +
        "input[aria-label*=\"pin code\" i],input[placeholder*=\"pin code\" i]," +
        "input[name*=\"account\" i],input[id*=\"account\" i]";

    // Collects backend node ids of protected inputs. Never throws: on any CDP error,
    // returns whatever was collected so far (value masking by LooksSensitive still applies).
    private static async Task<HashSet<long>?> ProtectedNodesAsync(CdpSession s, CancellationToken ct)
    {
        var result = new HashSet<long>();
        try
        {
            var doc = await s.SendAsync("DOM.getDocument", new { depth = 0 }, ct);
            var rootNodeId = doc.GetProperty("root").GetProperty("nodeId").GetInt64();

            var query = await s.SendAsync("DOM.querySelectorAll", new { nodeId = rootNodeId, selector = ProtectedSelector }, ct);
            if (!query.TryGetProperty("nodeIds", out var nodeIds) || nodeIds.ValueKind != JsonValueKind.Array)
                return null; // Protection cannot be determined: fail closed.

            foreach (var nid in nodeIds.EnumerateArray())
            {
                try
                {
                    var desc = await s.SendAsync("DOM.describeNode", new { nodeId = nid.GetInt64() }, ct);
                    if (desc.TryGetProperty("node", out var node)
                        && node.TryGetProperty("backendNodeId", out var bnid)
                        && bnid.TryGetInt64(out var backendId))
                        result.Add(backendId);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    // Node state unknown (e.g. detached meanwhile): fail closed.
                    return null;
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Protection cannot be determined: fail closed.
            return null;
        }
        return result;
    }

    // Heuristic for values that look like card numbers, IBANs or one-time/PIN codes.
    // Accessible names of fields whose values must never reach the agent (a CVC labelled only by aria-label, etc.).
    private static readonly Regex SensitiveNameRe = new(
        @"\b(?:pass(?:word|code)|pin|cv[cvn]|csc|security[\s_-]*code|card(?:[\s_-]*number)?|iban|account[\s_-]*number|one[\s_-]*time|otp|verification[\s_-]*code|authentication[\s_-]*code|2fa|sms[\s_-]*code|secret|token|api[\s_-]*key|social[\s_-]*security|ssn|bsn|rijksregister(?:nummer)?|national[\s_-]*number|wachtwoord|beveiligingscode|kaartnummer|rekeningnummer|verificatiecode)\b"
        + @"|пароль|код[\s_-]+подтверждения|код[\s_-]+из[\s_-]+смс|номер[\s_-]+карты|номер[\s_-]+сч[её]та",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static bool SensitiveName(string name) => !string.IsNullOrEmpty(name) && SensitiveNameRe.IsMatch(name);

    private static bool LooksSensitive(string v)
    {
        var compact = v.Replace(" ", "").Replace("-", "");
        if (compact.Length == 0) return false;

        var allDigits = true;
        foreach (var c in compact)
        {
            if (c < '0' || c > '9') { allDigits = false; break; }
        }

        // Card-like: 12-19 digits.
        if (allDigits && compact.Length >= 12 && compact.Length <= 19) return true;
        // Code-like: 4-8 digits.
        if (allDigits && compact.Length >= 4 && compact.Length <= 8) return true;
        // IBAN-like: country code, check digits, BBAN.
        return System.Text.RegularExpressions.Regex.IsMatch(compact, @"^[A-Za-z]{2}\d{2}[A-Za-z0-9]{10,30}$");
    }

    private static void Render(Dictionary<string, JsonElement> nodes, string id, int depth, string? parentName,
        StringBuilder sb, int maxChars, HashSet<long>? protectedIds, HashSet<string> seen, ref bool truncated)
    {
        if (truncated || !seen.Add(id) || !nodes.TryGetValue(id, out var n)) return;

        var role = Inner(n, "role") ?? "";
        var name = Inner(n, "name") ?? "";
        // Inline text boxes only repeat their StaticText parent; never render them.
        if (role == "InlineTextBox") return;

        var ignored = n.TryGetProperty("ignored", out var ig) && ig.ValueKind == JsonValueKind.True;
        var emit = !ignored && (name.Length > 0 || InteractiveRoles.Contains(role) || ShownWhenUnnamed.Contains(role));
        int childDepth = depth;
        string? childParentName = parentName;

        if (emit)
        {
            string? line = null;
            if (role == "StaticText")
            {
                if (name.Length > 0 && name != parentName) line = "- text: " + Clean(Cut(name, 150));
            }
            else
            {
                var line2 = new StringBuilder("- ").Append(role);
                if (name.Length > 0) line2.Append(" \"").Append(Quote(Cut(name, 150))).Append('"');
                if (ValueRoles.Contains(role) && Inner(n, "value") is { Length: > 0 } val)
                {
                    // Mask values of protected DOM inputs and values that look like secrets.
                    // A null set means protection is unknown: treat everything as protected.
                    var isProtected = protectedIds is null
                        || (n.ValueKind == JsonValueKind.Object
                            && n.TryGetProperty("backendDOMNodeId", out var bidProp)
                            && bidProp.TryGetInt64(out var nodeBid)
                            && protectedIds.Contains(nodeBid));
                    if (isProtected || SensitiveName(name) || LooksSensitive(val))
                        line2.Append(": \"***\"");
                    else
                        line2.Append(": \"").Append(Quote(Cut(val, 100))).Append('"');
                }
                AppendProps(n, line2);
                if (n.TryGetProperty("backendDOMNodeId", out var b) && b.TryGetInt64(out var bid))
                    line2.Append(" [ref=n").Append(bid).Append(']');
                line = line2.ToString();
                childDepth = depth + 1;
                childParentName = name;
            }

            if (line != null)
            {
                if (sb.Length > maxChars) { truncated = true; return; }
                sb.Append(' ', depth * 2).Append(line).Append('\n');
            }
            if (role == "StaticText") return; // its children are inline text boxes
        }

        if (!n.TryGetProperty("childIds", out var kids) || kids.ValueKind != JsonValueKind.Array) return;
        foreach (var k in kids.EnumerateArray())
        {
            if (truncated) return;
            if (k.ValueKind == JsonValueKind.String)
                Render(nodes, k.GetString()!, childDepth, childParentName, sb, maxChars, protectedIds, seen, ref truncated);
        }
    }

    private static void AppendProps(JsonElement n, StringBuilder line)
    {
        if (!n.TryGetProperty("properties", out var ps) || ps.ValueKind != JsonValueKind.Array) return;
        foreach (var p in ps.EnumerateArray())
        {
            var pname = Str(p, "name");
            var v = Inner(p, "value");
            if (pname == null) continue;
            if (StateProps.Contains(pname)) { if (v is "true" or "mixed") line.Append(" [").Append(pname).Append(']'); }
            else if (pname is "disabled" or "focused") { if (v == "true") line.Append(" [").Append(pname).Append(']'); }
            else if (pname == "level" && !string.IsNullOrEmpty(v)) line.Append(" [level=").Append(v).Append(']');
        }
    }

    // Reads obj.prop.value as text (AX values are wrapped as { type, value }).
    private static string? Inner(JsonElement obj, string prop) =>
        obj.TryGetProperty(prop, out var p) && p.ValueKind == JsonValueKind.Object && p.TryGetProperty("value", out var v)
            ? ValueText(v) : null;

    private static string ValueText(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.String => v.GetString() ?? "",
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Null or JsonValueKind.Undefined => "",
        _ => v.GetRawText(),
    };

    private static string Cut(string s, int max) => s.Length <= max ? s : s[..max] + "...";
    private static string Clean(string s) => s.Replace('\r', ' ').Replace('\n', ' ');
    private static string Quote(string s) => Clean(s).Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static IReadOnlyList<FormField> Fields(JsonElement v) =>
        v.ValueKind == JsonValueKind.Object && v.TryGetProperty("fields", out var fs) && fs.ValueKind == JsonValueKind.Array
            ? fs.EnumerateArray().Select(f => new FormField(
                Str(f, "label") ?? "", Str(f, "type") ?? "",
                f.TryGetProperty("value", out var fv) ? ValueText(fv) : "")).ToList()
            : Array.Empty<FormField>();

    public Task<ActionInfo> InspectAsync(string reference, CancellationToken ct)
    {
        RequireNotPaused();
        return RunAsync(async s =>
        {
            var node = ParseRef(reference);
            await RequireAllowedPageAsync(s, ct);
            var v = await CallAsync(s, node, AgentPageScripts.Classify, ct);
            var category = Str(v, "category");
            return new ActionInfo(string.IsNullOrEmpty(category) ? null : category, Str(v, "description") ?? "", Fields(v));
        }, ct);
    }

    // Classifies the focused element, so a key press (Enter, Space) on a send button is caught like a click.
    public Task<(ActionInfo Info, bool Editable)?> InspectFocusedAsync(CancellationToken ct)
    {
        RequireNotPaused();
        var expr = "(function(){ const el = document.activeElement; if (!el || el === document.body || el === document.documentElement) return null; const r = (" + AgentPageScripts.Classify + ").call(el); const t = (el.type || '').toLowerCase(); const ed = el.isContentEditable || el.tagName === 'TEXTAREA' || (el.tagName === 'INPUT' && !['button','submit','image','checkbox','radio','file','reset'].includes(t)); return Object.assign({}, r, { editable: ed }); })()";
        return RunAsync<(ActionInfo, bool)?>(async s =>
        {
            await RequireAllowedPageAsync(s, ct);
            var v = await EvalInWorldAsync(s, OpsWorld, expr, ct);
            if (v.ValueKind != JsonValueKind.Object)
                return null;
            var cat = Str(v, "category");
            var editable = v.TryGetProperty("editable", out var e) && e.ValueKind == JsonValueKind.True;
            return (new ActionInfo(string.IsNullOrEmpty(cat) ? null : cat, Str(v, "description") ?? "", Fields(v)), editable);
        }, ct);
    }

    // What pressing Enter in this field would do: classify the form's own submit button; a message-like field
    // (textarea, contenteditable, or named message/comment/reply/chat) without a submit button counts as sending.
    // 0.9.8: JS condition on `el`: the field is the search box of a dropdown list that is open right now.
    private const string OpenListJs = "(((el.getAttribute('role') === 'combobox' || ['list','both'].includes((el.getAttribute('aria-autocomplete') || '').toLowerCase())) && el.getAttribute('aria-expanded') === 'true') || !!el.closest('.select2-search, .select2-dropdown, .chosen-search, .chosen-drop'))";

    public Task<string?> EnterCategoryAsync(string? reference, CancellationToken ct) => RunAsync<string?>(async s =>
    {
        RequireNotPaused();
        await RequireAllowedPageAsync(s, ct);
        // 0.9.8: Enter in the search field of an open dropdown list (combobox or list autocomplete with
        // aria-expanded=true, select2/chosen search) picks an option: "choose". A message-like field never counts.
        var js = "function() { const el = this; const f = el.form || el.closest('form'); " +
            "const name = ((el.name || '') + ' ' + (el.id || '') + ' ' + (el.getAttribute('aria-label') || '') + ' ' + (el.placeholder || '')).toLowerCase(); " +
            "const msg = el.isContentEditable || el.tagName === 'TEXTAREA' || /message|comment|reply|chat|bericht|reactie|сообщ|коммент|ответ/.test(name); " +
            "if (!msg && el.tagName === 'INPUT' && " + OpenListJs + ") return 'choose'; " +
            "if (f) { const b = f.querySelector('button:not([type]), button[type=submit], input[type=submit], input[type=image]'); " +
            "if (b) { const c = (" + AgentPageScripts.Classify + ").call(b).category; return c || 'submit'; } " +
            "return msg ? 'send' : 'submit'; } return msg ? 'send' : null; }";
        JsonElement r;
        if (reference is null)
        {
            // No reference: use the focused element (ignore the document body).
            var expr = "(function(){ const el = document.activeElement; if (!el || el === document.body) return null; return (" + js + ").call(el); })()";
            r = await EvalInWorldAsync(s, OpsWorld, expr, ct);
        }
        else
        {
            var node = ParseRef(reference);
            r = await CallAsync(s, node, js, ct);
        }
        return r.ValueKind == JsonValueKind.String ? r.GetString() : null;
    }, ct);

    public Task<(bool InForm, IReadOnlyList<FormField> Fields)> ActiveFormAsync(CancellationToken ct) => RunAsync(async s =>
    {
        // EvalInWorldAsync returns the by-value result of the expression.
        await RequireAllowedPageAsync(s, ct);
        var v = await EvalInWorldAsync(s, OpsWorld, AgentPageScripts.ActiveForm, ct);
        var inForm = v.ValueKind == JsonValueKind.Object && v.TryGetProperty("inForm", out var f) && f.ValueKind == JsonValueKind.True;
        return (inForm, Fields(v));
    }, ct);

    public Task<PageInfo> ClickAsync(string reference, CancellationToken ct)
    {
        RequireNotPaused();
        return RunAsync(async s =>
        {
            var node = ParseRef(reference);
            await RequireActionAllowedAsync(s, ct);
            try { await s.SendAsync("DOM.scrollIntoViewIfNeeded", new { backendNodeId = node }, ct); }
            catch (Exception e) when (IsCdpError(e)) { /* not scrollable: use the box as is */ }

            var visible = await EnsureVisibleAsync(s, ct);
            if (!visible)
            {
                // Input.dispatchMouseEvent has no effect in a hidden tab; fall back to a synthetic DOM click.
                _lastBlock = null;
                Volatile.Write(ref _popup, null);
                var navHidden = Volatile.Read(ref _navSeq);
                await CallAsync(s, node, "function() { this.click(); return true; }", ct);
                Log("tab hidden: synthetic click used");
                await SettleAfterInputAsync(s, navHidden, ct);
                return await AfterNavigationAsync(s, null, ct);
            }
            // 0.9.8: when a real mouse click cannot land on the target (no box: a hidden custom radio or checkbox input;
            // the point is outside the window or covered by its label, a banner or another window), the element is
            // clicked from script instead of failing; on 07.11 the agent otherwise tabbed through forms key by key.
            var (x, y, why) = await ClickPointAsync(s, node, ct);
            if (why != null)
            {
                _lastBlock = null;
                Volatile.Write(ref _popup, null);
                var navSynthetic = Volatile.Read(ref _navSeq);
                await CallAsync(s, node, AgentPageScripts.OpenCombo, ct);
                Log("synthetic click used: " + why);
                await SettleAfterInputAsync(s, navSynthetic, ct);
                return await AfterNavigationAsync(s, null, ct);
            }

            _lastBlock = null;
            Volatile.Write(ref _popup, null);
            var navBefore = Volatile.Read(ref _navSeq);
            await s.SendAsync("Input.dispatchMouseEvent", new { type = "mouseMoved", x, y }, ct);
            await s.SendAsync("Input.dispatchMouseEvent", new { type = "mousePressed", x, y, button = "left", clickCount = 1 }, ct);
            await s.SendAsync("Input.dispatchMouseEvent", new { type = "mouseReleased", x, y, button = "left", clickCount = 1 }, ct);
            await SettleAfterInputAsync(s, navBefore, ct);
            return await AfterNavigationAsync(s, null, ct);
        }, ct);
    }

    // Centre of the element's box when a real mouse click there reaches the element; otherwise the reason why not.
    private async Task<(int X, int Y, string? Why)> ClickPointAsync(CdpSession s, long node, CancellationToken ct)
    {
        JsonElement q;
        try { q = await s.SendAsync("DOM.getContentQuads", new { backendNodeId = node }, ct); }
        catch (Exception e) when (IsCdpError(e)) { return (0, 0, "no box"); }
        if (!q.TryGetProperty("quads", out var quads) || quads.ValueKind != JsonValueKind.Array
            || quads.GetArrayLength() == 0 || quads[0].GetArrayLength() < 8)
            return (0, 0, "no visible box");

        var p = quads[0];
        double cx = (p[0].GetDouble() + p[2].GetDouble() + p[4].GetDouble() + p[6].GetDouble()) / 4;
        double cy = (p[1].GetDouble() + p[3].GetDouble() + p[5].GetDouble() + p[7].GetDouble()) / 4;
        int x = (int)cx, y = (int)cy;

        // Make sure the click lands on the target (or one of its descendants), not on an overlay.
        JsonElement hit;
        try { hit = await s.SendAsync("DOM.getNodeForLocation", new { x, y, includeUserAgentShadowDOM = true }, ct); }
        catch (Exception e) when (IsCdpError(e)) { return (0, 0, "point outside the page"); }
        var hitId = hit.GetProperty("backendNodeId").GetInt64();
        var ctx = await WorldAsync(s, OpsWorld, ct);
        // LinkedIn buttons on a member's Mac, 10.11: Node.contains threw "parameter 1 is not of type 'Node'" for the hit node and the
        // whole click failed (not reproduced here). Both nodes are resolved in one context, and a failed check (null) means a
        // synthetic click instead of an error.
        var hitObj = await ObjectIdAsync(s, hitId, ctx, ct);
        string? targetObj = null;
        JsonElement res = default;
        try
        {
            targetObj = await ObjectIdAsync(s, node, ctx, ct);
            var r = await s.SendAsync("Runtime.callFunctionOn", new
            {
                functionDeclaration = AgentPageScripts.Contains,
                objectId = targetObj,
                arguments = new object[] { new { objectId = hitObj } },
                returnByValue = true,
            }, ct);
            if (!r.TryGetProperty("exceptionDetails", out _) && r.TryGetProperty("result", out var rr) && rr.TryGetProperty("value", out var v))
                res = v.Clone();
        }
        catch (Exception e) when (IsCdpError(e)) { /* hit test failed: synthetic click below */ }
        finally
        {
            await ReleaseAsync(s, hitObj);
            if (targetObj != null) await ReleaseAsync(s, targetObj);
        }
        if (res.ValueKind == JsonValueKind.True) return (x, y, null);
        return (0, 0, res.ValueKind == JsonValueKind.False ? "covered by another element" : "hit test failed");
    }

    public Task<PageInfo> TypeAsync(string reference, string text, bool submit, CancellationToken ct)
    {
        RequireNotPaused();
        return RunAsync(async s =>
        {
            var node = ParseRef(reference);
            await RequireActionAllowedAsync(s, ct);
            var f = await CallAsync(s, node, AgentPageScripts.Field, ct);
            if (f.ValueKind == JsonValueKind.Object && f.TryGetProperty("sensitive", out var sens) && sens.ValueKind == JsonValueKind.True)
                throw new ToolException("sensitive_field", "This field takes a secret or payment data (" + (Str(f, "reason") ?? "sensitive")
                    + "); the agent never types there. Ask the user to fill it in the CO browser.");

            // Input.insertText works in a hidden tab, but the submit key press (Enter) does not.
            var typeVisible = await EnsureVisibleAsync(s, ct);
            await s.SendAsync("DOM.focus", new { backendNodeId = node }, ct);
            var dated = await CallAsync(s, node, AgentPageScripts.SetDateLike, ct, text);
            if (dated.ValueKind == JsonValueKind.Object && dated.TryGetProperty("handled", out var h) && h.ValueKind == JsonValueKind.True)
            {
                if (!dated.TryGetProperty("ok", out var dok) || dok.ValueKind != JsonValueKind.True)
                    throw new ToolException("bad_date", "This is a date/time field; '" + text + "' is not a value it takes. Type it like "
                        + (Str(dated, "expected") ?? "2026-12-01") + " (day-first 01/12/2026 also works for dates).");
                Log("date field set to " + Str(dated, "value"));
            }
            else
            {
                await CallAsync(s, node, AgentPageScripts.ClearValue, ct);
                await s.SendAsync("Input.insertText", new { text }, ct);
            }
            if (submit)
            {
                _lastBlock = null;
                Volatile.Write(ref _popup, null);
                var navBefore = Volatile.Read(ref _navSeq);
                // 0.9.8: a hidden or occluded tab gets synthetic key events instead of a tab_hidden error.
                if (typeVisible) await KeyAsync(s, "Enter", ct);
                else await SyntheticKeyAsync(s, "Enter", ct);
                await SettleAfterInputAsync(s, navBefore, ct);
            }
            else
            {
                await WaitSettledAsync(s, ct);
            }
            return await AfterNavigationAsync(s, null, ct);
        }, ct);
    }

    public Task<PageInfo> SelectAsync(string reference, string value, CancellationToken ct)
    {
        RequireNotPaused();
        return RunAsync(async s =>
        {
            var node = ParseRef(reference);
            await RequireActionAllowedAsync(s, ct);
            var v = await CallAsync(s, node, AgentPageScripts.SelectOption, ct, value);
            if (v.ValueKind == JsonValueKind.Object && v.TryGetProperty("notSelect", out var ns) && ns.ValueKind == JsonValueKind.True)
                v = await ChooseCustomAsync(s, node, value, ct);   // 0.9.8: radios, comboboxes, custom lists
            if (v.ValueKind != JsonValueKind.Object || !v.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True)
            {
                var detail = v.ValueKind == JsonValueKind.Object && v.TryGetProperty("options", out var opts) && opts.ValueKind == JsonValueKind.Array
                    ? string.Join(", ", opts.EnumerateArray().Select(o => o.ValueKind == JsonValueKind.Object
                        ? Str(o, "text") ?? Str(o, "value") ?? "" : ValueText(o)))
                    : (v.ValueKind == JsonValueKind.Object ? Str(v, "error") : null) ?? "unknown";
                throw new ToolException("no_option", "No such option. Options: " + detail);
            }
            return await InfoAsync(s, ct);
        }, ct);
    }

    // 0.9.8: radio button or group: pick by label; otherwise a combobox-like control: open it, type the wanted text
    // into it when it takes text, then click the matching visible option.
    private async Task<JsonElement> ChooseCustomAsync(CdpSession s, long node, string value, CancellationToken ct)
    {
        var radio = await CallAsync(s, node, AgentPageScripts.PickRadio, ct, value);
        if (radio.ValueKind == JsonValueKind.Object && radio.TryGetProperty("radios", out var hasRadios) && hasRadios.ValueKind == JsonValueKind.True)
        {
            await WaitSettledAsync(s, ct);
            return radio;
        }
        var open = await CallAsync(s, node, AgentPageScripts.OpenCombo, ct);
        await Task.Delay(400, ct);
        bool takesText = open.ValueKind == JsonValueKind.Object && open.TryGetProperty("input", out var inp) && inp.ValueKind == JsonValueKind.True;
        if (!takesText)
        {
            // A list with its own search field that got the focus when it opened (only such a field is cleared).
            var focused = await CallAsync(s, node, AgentPageScripts.DropdownSearchFocused, ct);
            takesText = focused.ValueKind == JsonValueKind.True;
            if (takesText)
                await EvalInWorldAsync(s, OpsWorld, "(function(){ const e = document.activeElement; const set = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value')?.set; if (set) set.call(e, ''); else e.value = ''; e.dispatchEvent(new Event('input', { bubbles: true })); return true; })()", ct);
        }
        else
        {
            await s.SendAsync("DOM.focus", new { backendNodeId = node }, ct);
            await CallAsync(s, node, AgentPageScripts.ClearValue, ct);
        }
        JsonElement picked = default;
        if (!takesText)
            picked = await CallAsync(s, node, AgentPageScripts.PickListOption, ct, value);
        if (takesText || picked.ValueKind != JsonValueKind.Object || !picked.TryGetProperty("ok", out var ok0) || ok0.ValueKind != JsonValueKind.True)
        {
            if (takesText) await s.SendAsync("Input.insertText", new { text = value }, ct);
            // Lists filled by the site after typing: up to 3 s.
            for (int i = 0; i < 6; i++)
            {
                await Task.Delay(500, ct);
                picked = await CallAsync(s, node, AgentPageScripts.PickListOption, ct, value);
                if (picked.ValueKind == JsonValueKind.Object && picked.TryGetProperty("ok", out var ok1) && ok1.ValueKind == JsonValueKind.True)
                    break;
            }
        }
        await WaitSettledAsync(s, ct);
        return picked;
    }

    public Task<PageInfo> PressAsync(string key, CancellationToken ct)
    {
        RequireNotPaused();
        return RunAsync(async s =>
        {
            await RequireActionAllowedAsync(s, ct);
            // Key events are not delivered to a hidden tab: 0.9.8 dispatches synthetic ones there instead of failing.
            var visible = await EnsureVisibleAsync(s, ct);
            _lastBlock = null;
            Volatile.Write(ref _popup, null);
            var navBefore = Volatile.Read(ref _navSeq);
            if (visible) await KeyAsync(s, key, ct);
            else
            {
                await SyntheticKeyAsync(s, key, ct);
                Log("tab hidden: synthetic key " + key);
            }
            await SettleAfterInputAsync(s, navBefore, ct);
            return await AfterNavigationAsync(s, null, ct);
        }, ct);
    }

    private async Task SyntheticKeyAsync(CdpSession s, string key, CancellationToken ct)
    {
        if (!Keys.TryGetValue(key ?? "", out var k))
            throw new ToolException("bad_key", "Supported keys: " + string.Join(", ", Keys.Keys) + ".");
        var expr = AgentPageScripts.SyntheticKey
            .Replace("(KEY, CODE, VK)", "(" + JsonSerializer.Serialize(k.Key) + ", " + JsonSerializer.Serialize(k.Code) + ", "
                + k.Vk.ToString(System.Globalization.CultureInfo.InvariantCulture) + ")");
        var r = await EvalInWorldAsync(s, OpsWorld, expr, ct);
        if (r.ValueKind != JsonValueKind.Object || !r.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True)
            throw new ToolException("tab_hidden", "The CO browser window is minimized, so the key " + key + " does not reach the page. " +
                "Use browser_type or browser_click instead, or ask the user to show the CO browser window.");
    }

    private static async Task KeyAsync(CdpSession s, string key, CancellationToken ct)
    {
        if (!Keys.TryGetValue(key ?? "", out var k))
            throw new ToolException("bad_key", "Supported keys: " + string.Join(", ", Keys.Keys) + ".");

        object down = k.Text != null
            ? new { type = "keyDown", key = k.Key, code = k.Code, windowsVirtualKeyCode = k.Vk, text = k.Text }
            : new { type = "rawKeyDown", key = k.Key, code = k.Code, windowsVirtualKeyCode = k.Vk };
        await s.SendAsync("Input.dispatchKeyEvent", down, ct);
        await s.SendAsync("Input.dispatchKeyEvent",
            new { type = "keyUp", key = k.Key, code = k.Code, windowsVirtualKeyCode = k.Vk }, ct);
    }

    public Task<PageInfo> ScrollAsync(int dy, CancellationToken ct)
    {
        RequireNotPaused();
        return RunAsync(async s =>
        {
            await RequireActionAllowedAsync(s, ct);
            if (!await EnsureVisibleAsync(s, ct))
            {
                // Wheel events do not reach a hidden tab: scroll from script instead.
                await EvalInWorldAsync(s, OpsWorld, "window.scrollBy(0, " + dy.ToString(System.Globalization.CultureInfo.InvariantCulture) + "), true", ct);
                Log("tab hidden: scrolled from script");
                await Task.Delay(300, ct);
                return await InfoAsync(s, ct);
            }
            var m = await s.SendAsync("Page.getLayoutMetrics", null, ct);
            var vp = m.GetProperty("cssLayoutViewport");
            var w = vp.GetProperty("clientWidth").GetDouble();
            var h = vp.GetProperty("clientHeight").GetDouble();
            await s.SendAsync("Input.dispatchMouseEvent",
                new { type = "mouseWheel", x = w / 2, y = h / 2, deltaX = 0, deltaY = dy }, ct);
            await Task.Delay(300, ct);
            return await InfoAsync(s, ct);
        }, ct);
    }

    public Task<PageInfo> UploadAsync(string reference, string fullPath, CancellationToken ct)
    {
        RequireNotPaused();
        return RunAsync(async s =>
        {
            var node = ParseRef(reference);
            await RequireActionAllowedAsync(s, ct);
            try { await s.SendAsync("DOM.setFileInputFiles", new { files = new[] { fullPath }, backendNodeId = node }, ct); }
            catch (Exception e) when (IsCdpError(e))
            {
                // co-resident 0.3.4: upload widgets hide the input behind a button ("Select files" on Homerun, 09.11) and the
                // snapshot shows only the button; take the file input of its label or the one nearest to it.
                // 0.3.5: the input of the element itself first, then the file dialog its click opens (the page picks the
                // input), and only then a guess among the inputs around it.
                var input = await FileInputNearAsync(s, node, true, ct);
                if (input == null && await UploadViaChooserAsync(s, node, fullPath, ct)) return await InfoAsync(s, ct);
                input ??= await FileInputNearAsync(s, node, false, ct) ?? throw new ToolException("not_file_input",
                    "No file input belongs to this ref and clicking it opened no file dialog; pick the 'Choose file' element or the upload button from the snapshot.");
                try { await s.SendAsync("DOM.setFileInputFiles", new { files = new[] { fullPath }, objectId = input }, ct); }
                finally { await ReleaseAsync(s, input); }
                Log("upload: used the file input next to n" + node);
            }
            return await InfoAsync(s, ct);
        }, ct);
    }

    // co-resident 0.3.5: the input is created only on click or lives where no ref leads (itds.nl Gravity Forms, 09.11): click the
    // element with the file dialog intercepted and give the files to the input that opened it. False when no dialog came in 5 s.
    private async Task<bool> UploadViaChooserAsync(CdpSession s, long node, string fullPath, CancellationToken ct)
    {
        var opened = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnChooser(string m, JsonElement p)
        {
            if (m == "Page.fileChooserOpened" && p.TryGetProperty("backendNodeId", out var b)) opened.TrySetResult(b.GetInt64());
        }
        s.EventReceived += OnChooser;
        try
        {
            await s.SendAsync("Page.setInterceptFileChooserDialog", new { enabled = true }, ct);
            try { await s.SendAsync("DOM.scrollIntoViewIfNeeded", new { backendNodeId = node }, ct); }
            catch (Exception e) when (IsCdpError(e)) { }
            var (x, y, why) = await EnsureVisibleAsync(s, ct) ? await ClickPointAsync(s, node, ct) : (0, 0, "tab hidden");
            if (why == null)
            {
                await s.SendAsync("Input.dispatchMouseEvent", new { type = "mouseMoved", x, y }, ct);
                await s.SendAsync("Input.dispatchMouseEvent", new { type = "mousePressed", x, y, button = "left", clickCount = 1 }, ct);
                await s.SendAsync("Input.dispatchMouseEvent", new { type = "mouseReleased", x, y, button = "left", clickCount = 1 }, ct);
            }
            else
            {
                // A file dialog opens only on a user gesture; Runtime.callFunctionOn can carry one.
                var ctx = await WorldAsync(s, OpsWorld, ct);
                var obj = await ObjectIdAsync(s, node, ctx, ct);
                try { await s.SendAsync("Runtime.callFunctionOn", new { functionDeclaration = "function() { this.click(); }", objectId = obj, userGesture = true }, ct); }
                finally { await ReleaseAsync(s, obj); }
            }
            var done = await Task.WhenAny(opened.Task, Task.Delay(5000, ct));
            if (done != opened.Task) return false;
            await s.SendAsync("DOM.setFileInputFiles", new { files = new[] { fullPath }, backendNodeId = opened.Task.Result }, ct);
            Log("upload: file dialog of n" + node + " intercepted (" + (why ?? "mouse click") + ")");
            return true;
        }
        finally
        {
            s.EventReceived -= OnChooser;
            try { await s.SendAsync("Page.setInterceptFileChooserDialog", new { enabled = false }, CancellationToken.None); }
            catch (Exception) { }
        }
    }

    // Remote object id of the file input that belongs to the element, null when there is none or it is ambiguous.
    private static async Task<string?> FileInputNearAsync(CdpSession s, long node, bool own, CancellationToken ct)
    {
        var ctx = await WorldAsync(s, OpsWorld, ct);
        var objectId = await ObjectIdAsync(s, node, ctx, ct);
        try
        {
            var r = await s.SendAsync("Runtime.callFunctionOn", new
            {
                functionDeclaration = AgentPageScripts.FileInputNear,
                objectId,
                arguments = new object[] { new { value = own } },
                returnByValue = false,
            }, ct);
            ThrowOnException(r);
            return r.TryGetProperty("result", out var res) && Str(res, "objectId") is { Length: > 0 } id ? id : null;
        }
        finally { await ReleaseAsync(s, objectId); }
    }
}

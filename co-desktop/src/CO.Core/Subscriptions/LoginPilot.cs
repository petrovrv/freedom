// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json;
using CO.Core.Browser;

namespace CO.Core.Subscriptions;

// S4: drives the provider's sign-in page in the CO browser (the profile where the user is signed in to the sites):
// opens the CLI's link, picks the user's account, types the e-mail of the subscription and takes the sign-in link or
// code from the user's own mailbox (IMailCodes, e.g. Gmail in the same browser), presses the consent button, types the
// codex device code, and hands the claude callback code to the CLI. It never types a password: such a page waits for
// the person at the computer or for an agent (device tool cli_subs + the agent browser).
public static class LoginPilot
{
    // Consent buttons, matched on the whole (lower-case) label only.
    private static readonly string[] ClaudeWords = { "authorize", "allow", "approve", "разрешить", "autoriser", "toestaan", "autorisieren", "zulassen" };
    private static readonly string[] CodexWords = { "continue", "allow", "authorize", "approve", "продолжить", "разрешить", "doorgaan", "continuer", "weiter" };

    // The sign-in pages of each provider, by exact host: the pilot acts nowhere else.
    public static readonly IReadOnlyDictionary<string, string[]> Hosts = new Dictionary<string, string[]>
    {
        ["claude"] = new[] { "claude.ai", "claude.com", "platform.claude.com", "console.anthropic.com" },
        ["codex"] = new[] { "auth.openai.com" },
    };

    // The callback page of this sign-in: provider host, a code, and the state of the CLI's own link.
    public static bool IsClaudeCallback(string url, string state = "") =>
        OwnPage("claude", url) && Uri.TryCreate(url, UriKind.Absolute, out var u)
        && u.AbsolutePath.EndsWith("/oauth/code/callback", StringComparison.OrdinalIgnoreCase)
        && CliCatalog.Query(u.Query).TryGetValue("code", out string? c) && c.Length > 0
        && (state.Length == 0 || CliCatalog.StateOf(url) == state);

    public static bool OwnPage(string cli, string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == "https" && Hosts.TryGetValue(cli, out var hosts)
        && hosts.Contains(u.Host.ToLowerInvariant());

    // A sign-in link from the provider's mail that the pilot may open: claude.ai magic links only.
    public static bool IsMagicLink(string cli, string url) =>
        cli == "claude" && Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == "https"
        && u.Host.Equals("claude.ai", StringComparison.OrdinalIgnoreCase) && u.AbsolutePath.StartsWith("/magic-link", StringComparison.OrdinalIgnoreCase);

    // One step on the page. Results: "login" (a password or an unknown account is needed), "email" (typed the e-mail and
    // asked for a mail), "await-mail" (the page waits for the mailed link or code), "clicked:<label>", "filled", "".
    // jsClick false: the script only reports where the button is ("<result>@x,y") and the pilot clicks with the mouse.
    public static string StepScript(string cli, string userCode, string email, bool emailAllowed, bool jsClick = true, bool typeCode = false)
    {
        string words = JsonSerializer.Serialize(cli == "claude" ? ClaudeWords : CodexWords);
        return "(() => {" +
               $"if (!{JsonSerializer.Serialize(Hosts[cli])}.includes(location.hostname)) return '';" +
               "const vis = e => !!(e && e.offsetParent !== null && !e.disabled);" +
               "const label = e => (e.innerText || e.value || e.getAttribute('aria-label') || '').trim().replace(/\\s+/g, ' ');" +
               "const pos = (b, what) => { b.scrollIntoView({block: 'center'}); const r = b.getBoundingClientRect(); return what + '@' + Math.round(r.left + r.width / 2) + ',' + Math.round(r.top + r.height / 2); };" +
               "const hit = (b, what) => " + (jsClick ? "{ b.click(); return what; }" : "pos(b, what)") + ";" +
               "const set = (i, v) => { Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set.call(i, v); i.dispatchEvent(new Event('input', {bubbles: true})); i.dispatchEvent(new Event('change', {bubbles: true})); };" +
               "const ins = [...document.querySelectorAll('input')].filter(vis);" +
               "const btns = [...document.querySelectorAll('button, [role=button], input[type=submit]')].filter(vis);" +
               $"const email = {JsonSerializer.Serialize(email.ToLowerInvariant())};" +
               $"const code = {JsonSerializer.Serialize(userCode)};" +
               "const text = (document.body ? document.body.innerText : '').toLowerCase();" +
               "if (ins.some(i => i.type === 'password')) return 'login';" +
               // account chooser (OpenAI "Choose an account"): the subscription's account, or the only one listed
               "const accts = btns.filter(b => /^select account/i.test(label(b)));" +
               "if (accts.length) { const a = email ? accts.find(b => label(b).toLowerCase().includes(email)) : (accts.length === 1 ? accts[0] : null);" +
               "  if (a) return hit(a, 'clicked:account'); return 'login'; }" +
               // claude: the mail with the sign-in link was sent
               "if (/link sent to|check your (e-?mail|inbox)|verification code/.test(text) && ins.some(i => /code/i.test(i.placeholder || i.getAttribute('aria-label') || i.name || ''))) return 'await-mail';" +
               "const mail = ins.find(i => i.type === 'email' || /email/i.test(i.name || i.autocomplete || i.placeholder || ''));" +
               "if (mail) {" +
               $"  if (!email || !{(emailAllowed ? "true" : "false")}) return 'login';" +
               "  if (mail.value.toLowerCase() !== email) set(mail, email);" +
               "  const go = btns.find(b => /^continue with e-?mail$|^continue$/i.test(label(b)));" +
               "  if (!go) return 'login';" +
               "  return hit(go, 'email');" +
               "}" +
               "if (code) {" +
               // S7: the code stays in sight on the page itself (the person sees what CO types, or types it)
               "  if (document.body && !document.getElementById('co-code-hint')) { const d = document.createElement('div'); d.id = 'co-code-hint';" +
               "    d.textContent = 'CO Desktop: code ' + code; d.style.cssText = 'position:fixed;top:10px;right:10px;z-index:2147483647;background:#e8730c;color:#fff;font:bold 22px/1.3 monospace;padding:8px 14px;border-radius:8px;pointer-events:none';" +
               "    document.body.appendChild(d); }" +
               "  const want = code.replace(/[^A-Z0-9]/gi, '').toUpperCase();" +
               "  const boxes = ins.filter(i => (i.type === 'text' || i.type === '' || i.type === 'tel') && !i.readOnly);" +
               "  const have = boxes.map(i => i.value).join('').replace(/[^A-Z0-9]/gi, '').toUpperCase();" +
               // S7: auth.openai.com takes the 9 boxes only through its paste handler (typed or set values never reach its
               // hidden user_code): a paste event with the code on the first box, once per page; the old ways after that
               "  if (boxes.length > 1 && have !== want && !document.body.dataset.coPasted) {" +
               "    document.body.dataset.coPasted = '1'; boxes[0].focus(); const dt = new DataTransfer(); dt.setData('text/plain', want);" +
               "    boxes[0].dispatchEvent(new ClipboardEvent('paste', {clipboardData: dt, bubbles: true, cancelable: true})); return 'filled'; }" +
               "  if (boxes.length && have !== want) {" +
               (!typeCode ? "    if (boxes.length === 1) { set(boxes[0], code); return 'filled'; }" +
                          "    if (boxes.length >= want.length) { want.split('').forEach((ch, k) => set(boxes[k], ch)); return 'filled'; }"
                        : "    boxes.forEach(i => set(i, '')); boxes[0].focus(); return 'type@0,0';") +
               "  }" +
               "}" +
               $"const words = {words};" +
               "for (const b of btns) { const t = label(b).toLowerCase(); if (words.includes(t)) return hit(b, 'clicked:' + t); }" +
               "return '';" +
               "})()";
    }

    // Types a mailed verification code into the page and presses its button.
    public static string MailCodeScript(string cli, string code) =>
        "(() => {" +
        $"if (!{JsonSerializer.Serialize(Hosts[cli])}.includes(location.hostname)) return '';" +
        "const vis = e => !!(e && e.offsetParent !== null && !e.disabled);" +
        "const i = [...document.querySelectorAll('input')].filter(vis).find(x => /code/i.test(x.placeholder || x.getAttribute('aria-label') || x.name || x.autocomplete || ''));" +
        "if (!i) return '';" +
        $"Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set.call(i, {JsonSerializer.Serialize(code)});" +
        "i.dispatchEvent(new Event('input', {bubbles: true}));" +
        "const b = [...document.querySelectorAll('button')].filter(vis).find(x => /verify|continue|submit/i.test(x.innerText || ''));" +
        "if (b) b.click(); return 'typed';" +
        "})()";

    // A real mouse click (CDP Input events) on the tab brought to the front; other browsers (tests) get a script click.
    public static async Task ClickAsync(IBrowser tab, double x, double y, CancellationToken ct)
    {
        if (tab is EdgeBrowser.EdgeTab et)
        {
            await et.WithSessionAsync(async p =>
            {
                await p.SendAsync("Page.bringToFront", null, ct);
                foreach (string type in new[] { "mouseMoved", "mousePressed", "mouseReleased" })
                    await p.SendAsync("Input.dispatchMouseEvent", new { type, x, y, button = type == "mouseMoved" ? "none" : "left", buttons = type == "mousePressed" ? 1 : 0, clickCount = 1 }, ct);
                return 0;
            }, ct);
            return;
        }
        string px = x.ToString(System.Globalization.CultureInfo.InvariantCulture), py = y.ToString(System.Globalization.CultureInfo.InvariantCulture);
        await tab.EvaluateAsync($"(() => {{ const e = document.elementFromPoint({px}, {py}); if (e) e.click(); return ''; }})()", ct);
    }

    // Restores a minimized CO browser window and brings the tab to the front.
    private static async Task ShowAsync(IBrowser tab, CancellationToken ct)
    {
        if (tab is not EdgeBrowser.EdgeTab et) { await tab.BringToFrontAsync(ct); return; }
        await et.WithSessionAsync(async p =>
        {
            try
            {
                var w = await p.SendAsync("Browser.getWindowForTarget", null, ct);
                if (w.ValueKind == JsonValueKind.Object && w.TryGetProperty("windowId", out var id))
                    await p.SendAsync("Browser.setWindowBounds", new { windowId = id.GetInt32(), bounds = new { windowState = "normal" } }, ct);
            }
            catch (InvalidOperationException) { }   // the Browser domain is not offered on this session
            await p.SendAsync("Page.bringToFront", null, ct);
            return 0;
        }, ct);
    }

    private static async Task TypeAsync(IBrowser tab, string text, CancellationToken ct)
    {
        if (tab is not EdgeBrowser.EdgeTab et) return;
        await et.WithSessionAsync(async p =>
        {
            await p.SendAsync("Page.bringToFront", null, ct);
            foreach (char ch in text)
            {
                // insertText commits the character to the focused box as typed input (the page then moves the focus on)
                await p.SendAsync("Input.insertText", new { text = ch.ToString() }, ct);
                await Task.Delay(80, ct);
            }
            return 0;
        }, ct);
    }

    public static TimeSpan ActionPause { get; set; } = TimeSpan.FromSeconds(6);

    public static async Task RunAsync(IBrowser tab, LoginSession s, Func<string, string> submitCode, Action<string> note, CancellationToken ct,
        IMailCodes? mail = null)
    {
        await tab.NavigateAsync(s.Url, ct);
        try { await tab.BringToFrontAsync(ct); } catch (Exception) { }
        note("page opened in the CO browser");
        var clicks = new Dictionary<string, int>(StringComparer.Ordinal);
        var acted = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        bool askedLogin = false, shown = false;
        DateTime nextShow = DateTime.MinValue;
        int emails = 0;
        DateTime? mailSince = null;
        DateTime nextMail = DateTime.MinValue;
        var until = DateTime.UtcNow + SubscriptionManager.LoginLimit;
        while (!s.Ended && DateTime.UtcNow < until)
        {
            await Task.Delay(1500, ct);
            string url;
            try { url = await tab.CurrentUrlAsync(ct); }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { continue; }   // the tab is navigating or was closed by the person; try again
            if (s.Cli == "claude" && IsClaudeCallback(url, CliCatalog.StateOf(s.Url)))
            {
                note(submitCode(url));
                return;
            }
            if (!OwnPage(s.Cli, url)) continue;
            // at most 3 actions per page: a button that does nothing must not be pressed forever
            string page = url.Split('?')[0];
            if (clicks.GetValueOrDefault(page) >= 4) continue;
            // a page that is still submitting after a click is not clicked again for a few seconds
            if (acted.TryGetValue(page, out var last) && DateTime.UtcNow - last < ActionPause) continue;
            // a hidden tab never finishes the invisible captcha that unlocks claude's consent button: show the window
            if (DateTime.UtcNow >= nextShow)
            {
                nextShow = DateTime.UtcNow.AddSeconds(10);
                try
                {
                    var vis = await tab.EvaluateAsync("document.visibilityState", ct);
                    if (vis.ValueKind == JsonValueKind.String && vis.GetString() == "hidden")
                    {
                        await ShowAsync(tab, ct);
                        if (!shown) note("the CO browser window must be visible for the sign-in page (screen on, window not minimized)");
                        shown = true;
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception) { }
            }
            string r;
            try
            {
                // pages differ: some ignore script clicks (claude.ai), some mouse events (auth.openai.com); alternate
                bool js = clicks.GetValueOrDefault(page) % 2 == 0;
                var v = await tab.EvaluateAsync(StepScript(s.Cli, s.Cli == "codex" ? s.UserCode : "", s.Email, emails < 2, js, tab is EdgeBrowser.EdgeTab), ct);
                r = v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
                int at = r.IndexOf('@');
                if (at > 0)
                {
                    string[] xy = r[(at + 1)..].Split(',');
                    r = r[..at];
                    if (r != "type")
                        await ClickAsync(tab, double.Parse(xy[0], System.Globalization.CultureInfo.InvariantCulture), double.Parse(xy[1], System.Globalization.CultureInfo.InvariantCulture), ct);
                    else
                    {
                        // the code box has the focus: type the code key by key, like a person
                        await TypeAsync(tab, s.UserCode.Replace("-", ""), ct);
                        r = "filled";
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { continue; }
            if (r == "login")
            {
                if (!askedLogin) note("sign in to " + CliCatalog.Get(s.Cli).Provider + " in the CO browser window; CO continues after that");
                askedLogin = true;
            }
            else if (r == "email")
            {
                emails++;
                mailSince = DateTime.UtcNow.AddMinutes(-1);
                nextMail = DateTime.UtcNow.AddSeconds(8);
                note("e-mail " + s.Email + " typed; waiting for the sign-in mail");
            }
            else if (r == "await-mail")
            {
                mailSince ??= DateTime.UtcNow.AddMinutes(-10);
                if (mail == null || s.Email.Length == 0)
                {
                    if (!askedLogin) note("open the sign-in link from your mail in the CO browser (or type its code on the page)");
                    askedLogin = true;
                    continue;
                }
                if (DateTime.UtcNow < nextMail) continue;
                nextMail = DateTime.UtcNow.AddSeconds(10);
                MailHit? hit;
                try { hit = await mail.FindAsync(s.Cli, s.Email, mailSince.Value, ct); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { note("mail: " + CliCatalog.Cut(ex.Message, 160)); continue; }
                if (hit == null) continue;
                if (hit.Link.Length > 0 && IsMagicLink(s.Cli, hit.Link))
                {
                    note("sign-in link found in the mail; opening it");
                    await tab.NavigateAsync(hit.Link, ct);
                }
                else if (hit.Code.Length > 0)
                {
                    note("code from the mail typed on the page");
                    await tab.EvaluateAsync(MailCodeScript(s.Cli, hit.Code), ct);
                }
                mailSince = DateTime.UtcNow;   // a second mail is a new one
                clicks[page] = clicks.GetValueOrDefault(page) + 1;
            }
            else if (r.StartsWith("clicked:", StringComparison.Ordinal) || r == "filled")
            {
                clicks[page] = clicks.GetValueOrDefault(page) + 1;
                if (r != "filled") acted[page] = DateTime.UtcNow;
                askedLogin = false;
                note(r == "filled" ? "device code typed on the page" : r == "clicked:account" ? "account chosen" : "pressed '" + r[8..] + "'");
            }
        }
    }
}

public sealed record MailHit(string Link, string Code);

// The user's own mailbox, read for the provider's sign-in mail sent to the subscription's address after a moment.
public interface IMailCodes
{
    Task<MailHit?> FindAsync(string cli, string toEmail, DateTime sinceUtc, CancellationToken ct);
}

// Gmail in the CO browser (the user is signed in there): search, open the newest matching mail, read the link or code
// from its body. Only the provider's mail to the subscription's address, sent after the pilot asked for it.
public sealed class GmailWebMail : IMailCodes
{
    private readonly Func<IBrowser> _tab;
    public GmailWebMail(Func<IBrowser> tab) => _tab = tab;

    public static string Query(string cli, string toEmail, DateTime sinceUtc) =>
        $"from:({(cli == "claude" ? "anthropic.com OR claude.ai OR claude.com" : "openai.com OR chatgpt.com")}) to:{toEmail} after:{new DateTimeOffset(sinceUtc).ToUnixTimeSeconds()}";

    public const string OpenNewestScript =
        "(() => { const r = document.querySelector('tr.zA'); if (!r) return 'none'; r.click(); return 'opened'; })()";

    public const string ReadBodyScript =
        "(() => { const bodies = [...document.querySelectorAll('div.a3s')]; if (!bodies.length) return null;" +
        " const b = bodies[bodies.length - 1];" +
        " const links = [...b.querySelectorAll('a[href]')].map(a => a.href);" +
        " return JSON.stringify({links, text: (b.innerText || '').slice(0, 4000)}); })()";

    public static MailHit? Parse(string cli, string json)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        foreach (var l in r.GetProperty("links").EnumerateArray())
        {
            string link = l.GetString() ?? "";
            if (LoginPilot.IsMagicLink(cli, link)) return new MailHit(link, "");
        }
        string text = r.GetProperty("text").GetString() ?? "";
        if (!System.Text.RegularExpressions.Regex.IsMatch(text, cli == "codex" ? "openai|chatgpt" : "claude|anthropic", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) return null;
        var m = System.Text.RegularExpressions.Regex.Match(text, @"(?<![\d#])(\d{6})(?!\d)");
        return m.Success ? new MailHit("", m.Groups[1].Value) : null;
    }

    public async Task<MailHit?> FindAsync(string cli, string toEmail, DateTime sinceUtc, CancellationToken ct)
    {
        var tab = _tab();
        // authuser: the Gmail account of that address when several are signed in (a forwarding mailbox falls back to the first)
        string hash = "#search/" + Uri.EscapeDataString(Query(cli, toEmail, sinceUtc));
        await tab.NavigateAsync("https://mail.google.com/mail/?authuser=" + Uri.EscapeDataString(toEmail) + hash, ct);
        await Task.Delay(2500, ct);   // the search replaces the list shown before
        var h = await tab.EvaluateAsync("location.hash", ct);
        if (h.ValueKind != JsonValueKind.String || !h.GetString()!.StartsWith("#search/", StringComparison.Ordinal)) return null;
        for (int i = 0; i < 12; i++)
        {
            await Task.Delay(1000, ct);
            var o = await tab.EvaluateAsync(OpenNewestScript, ct);
            if (o.ValueKind == JsonValueKind.String && o.GetString() == "opened") break;
            if (i == 11) return null;
        }
        for (int i = 0; i < 12; i++)
        {
            await Task.Delay(1000, ct);
            var b = await tab.EvaluateAsync(ReadBodyScript, ct);
            if (b.ValueKind == JsonValueKind.String) return Parse(cli, b.GetString()!);
        }
        return null;
    }
}

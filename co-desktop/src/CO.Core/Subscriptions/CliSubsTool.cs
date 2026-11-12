// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json;
using CO.Core.Policy;
using CO.Core.Tools;

namespace CO.Core.Subscriptions;

// S4: the agent side of the Subscriptions screen. An agent (usually the Site AI through the S1 relay) connects the
// device user's own CLI subscription without a manual step: install, start the sign-in, finish the provider's page
// in this device's agent browser (browser_* tools, same profile), read an e-mail code from the user's own mailbox if
// the provider asks for one, pass the callback code, check. Installing and starting a sign-in are code execution
// (class X, one confirmation per task unless the environment is trusted); the rest only reads or feeds the running
// sign-in.
public sealed class CliSubsTool : IDeviceTool
{
    private readonly SubscriptionManager _m;
    public CliSubsTool(SubscriptionManager m) => _m = m;

    private static readonly string[] Actions = { "status", "install", "login_start", "login_code", "login_status", "login_cancel", "verify", "register" };

    public ToolDescriptor Descriptor { get; } = new()
    {
        Name = "cli_subs", Class = RiskClass.X, Untrusted = UntrustedMode.Never,
        Description =
            "Agent CLI subscriptions of this computer's user (claude = Claude Code, codex = Codex, agy = Antigravity: status only). " +
            "action=status: installed/version/signed-in account/plan of each. install: install the CLI (official installer; codex via npm or its GitHub release). " +
            "login_start: start the CLI's sign-in; returns the provider page link (and for codex a one-time code) and opens it in the CO browser, " +
            "where CO presses the consent button itself when the user is already signed in to the provider. If the page asks to sign in first, " +
            "open the same link with browser_navigate (same browser profile), sign in with the user's own account (an e-mail code comes to the " +
            "user's own mailbox), then: claude — pass the code of the final page with login_code (the page link or 'code#state'); codex — CO pastes " +
            "the one-time code into the page itself (the 9 boxes take it only as a paste into the first box, not typed), the CLI finishes by itself. login_status: progress; login_cancel: stop. verify: one tiny request " +
            "through the subscription. register: report the subscriptions to the CO server now (also automatic after a sign-in).",
        InputSchema = Schema.Obj(
            ("action", "string", "status | install | login_start | login_code | login_status | login_cancel | verify | register", true),
            ("cli", "string", "claude | codex | agy (not for status/register)", false),
            ("code", "string", "login_code: the callback page link or code#state (claude)", false),
            ("email", "string", "login_start: the subscription's account e-mail (default: the last signed-in account); CO types it on the sign-in page and reads the provider's mail to it from Gmail in the CO browser", false)),
    };

    private static string Action(JsonElement a)
    {
        string act = Args.Str(a, "action");
        if (!Actions.Contains(act)) throw new ToolException("invalid_args", "action must be one of " + string.Join(", ", Actions));
        return act;
    }

    private static string Cli(JsonElement a)
    {
        string? cli = Args.OptStr(a, "cli");
        if (cli == null || !CliCatalog.IsKnown(cli)) throw new ToolException("invalid_args", "cli must be claude, codex or agy");
        return cli;
    }

    public ActionPlan Plan(JsonElement args, ToolEnv env)
    {
        string act = Action(args);
        if (act is "install" or "login_start" or "login_cancel")
        {
            string cli = Cli(args);
            var p = new ActionPlan
            {
                Tool = "cli_subs", Class = RiskClass.X,
                Summary = act switch
                {
                    "install" => $"install the {CliCatalog.Get(cli).Title} CLI on this computer",
                    "login_cancel" => $"stop the {CliCatalog.Get(cli).Title} sign-in",
                    _ => $"sign in to {CliCatalog.Get(cli).Title} with your own subscription",
                },
            };
            p.Scopes.Add(new Scope(ScopeKind.App, "cli:" + cli));
            return p;
        }
        return new ActionPlan { Tool = "cli_subs", Class = RiskClass.R, Summary = "subscriptions: " + act };
    }

    public async Task<ToolOutput> ExecuteAsync(JsonElement args, ToolEnv env, CancellationToken ct)
    {
        string act = Action(args);
        try
        {
            string text = act switch
            {
                "status" => Status(await _m.StatusAllAsync(ct)),
                "register" => await _m.RefreshAndReportAsync(ct) is { Length: > 0 } r ? r : "registration is not configured on this device",
                "install" => await _m.InstallAsync(Cli(args), ct),
                "login_start" => (await _m.StartLoginAsync(Cli(args), ct, email: Args.OptStr(args, "email"), owner: Owner(env))).Describe(),
                "login_code" => Mine(Cli(args), env) is { } _ ? _m.SubmitCode(Cli(args), Args.OptStr(args, "code") ?? throw new ToolException("invalid_args", "code is required"))
                    : throw new ToolException("not_yours", "only the agent run that started this sign-in may pass its code"),
                "login_status" => await LoginStatusAsync(Cli(args), env, ct),
                "login_cancel" => _m.Cancel(Cli(args)) ? "sign-in cancelled" : "no sign-in was running",
                "verify" => await _m.VerifyAsync(Cli(args), ct),
                _ => throw new ToolException("invalid_args", "unknown action"),
            };
            return new ToolOutput { Content = text };
        }
        catch (ToolException) { throw; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or TimeoutException or HttpRequestException or IOException)
        {
            throw new ToolException("subs_failed", ex.Message);
        }
    }

    // A sign-in belongs to the agent run that started it: only that run sees its link and code and may pass a code.
    private static string Owner(ToolEnv env) => "run:" + env.RunId;

    private LoginSession? Mine(string cli, ToolEnv env) => _m.Login(cli) is { } s && s.Owner == Owner(env) && !s.Ended ? s : null;

    // Waits up to 20 s for the sign-in to move on, so an agent polling it needs fewer calls.
    private async Task<string> LoginStatusAsync(string cli, ToolEnv env, CancellationToken ct)
    {
        var s = _m.Login(cli);
        if (s == null) return "no sign-in of " + cli + " was started";
        string before = s.State + "|" + s.Message;
        for (int i = 0; i < 40 && !s.Ended && s.State + "|" + s.Message == before; i++) await Task.Delay(500, ct);
        return s.Owner == Owner(env) ? s.Describe() : s.Brief() + " (started by " + (s.Owner == "local" ? "the person at this computer" : "another agent run") + ")";
    }

    private static string Status(IReadOnlyList<CliStatus> all) => string.Join("\n", all.Select(s => s.Line()));
}

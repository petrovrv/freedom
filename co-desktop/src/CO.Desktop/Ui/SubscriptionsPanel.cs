// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CO.Core.Subscriptions;

namespace CO.Desktop.Ui;

// S4: Subscriptions tab — the agent CLIs of this computer with the user's own subscriptions: install, sign in
// (the provider page opens in the CO browser and CO presses the consent button itself), check, and registration on
// the CO server as a model resource of the environment. Agents do the same through the device tool cli_subs.
public sealed class SubscriptionsPanel : StackPanel
{
    private readonly SubscriptionManager _m;
    private readonly Dictionary<string, TextBlock> _lines = new();
    private readonly Dictionary<string, TextBlock> _logins = new();
    private readonly TextBox _code = new() { Width = 360, Margin = new Thickness(0, 6, 6, 0) };
    private readonly TextBox _email = new() { Width = 260, Margin = new Thickness(0, 6, 6, 0) };
    private readonly TextBlock _server = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 2), Foreground = Brushes.DimGray };

    public static readonly TimeSpan Every = TimeSpan.FromHours(6);

    public SubscriptionsPanel(SubscriptionManager m)
    {
        _m = m;
        Margin = new Thickness(10);
        Children.Add(H("Subscriptions"));
        Children.Add(Text("Agent CLIs of this computer with your own subscription. Connect installs the CLI if needed, starts its sign-in " +
            "and opens the provider page in the CO browser; when you are signed in to the provider there, CO confirms by itself. " +
            "If the page asks you to sign in first, do it in that window (or let an agent do it) — CO continues after that. " +
            "Signed-in subscriptions are registered on the CO server for your environment."));
        Children.Add(Text("Account e-mail of the subscription (empty: the account this CLI used last). CO types it on the sign-in page and " +
            "takes the sign-in link from that mail in Gmail of the CO browser."));
        Children.Add(_email);
        foreach (var c in CliCatalog.All)
        {
            Children.Add(new TextBlock { Text = c.Title + " — " + c.Provider, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 0) });
            var line = new TextBlock { TextWrapping = TextWrapping.Wrap, Text = "not checked yet" };
            _lines[c.Id] = line;
            Children.Add(line);
            var login = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DarkSlateBlue, FontFamily = new FontFamily("Consolas") };
            _logins[c.Id] = login;
            Children.Add(login);
            var row = new WrapPanel();
            string id = c.Id;
            if (c.CanLogin)
            {
                row.Children.Add(Button("Connect", () => ConnectAsync(id)));
                row.Children.Add(Button("Cancel sign-in", () => { _m.Cancel(id); return Task.CompletedTask; }));
            }
            row.Children.Add(Button("Check", () => CheckAsync(id)));
            Children.Add(row);
        }
        Children.Add(H("Code of the final page"));
        Children.Add(Text("Only if CO did not pass it by itself: the claude page shows a code after you authorize. Paste it (or the page link) here."));
        var codeRow = new WrapPanel();
        codeRow.Children.Add(_code);
        codeRow.Children.Add(Button("Pass to Claude", () =>
        {
            string r = _m.SubmitCode("claude", _code.Text);
            _code.Text = "";
            MessageBox.Show(r, "Subscriptions");
            return Task.CompletedTask;
        }));
        Children.Add(codeRow);
        Children.Add(H("CO server"));
        Children.Add(_server);
        Children.Add(Button("Refresh and register now", async () => _server.Text = await Task.Run(() => _m.RefreshAndReportAsync(CancellationToken.None))));
        _m.Changed += () => Dispatcher.BeginInvoke(Show);
    }

    public async Task RunLoopAsync(CancellationToken ct)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(20), ct); } catch (OperationCanceledException) { return; }
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Run(() => _m.RefreshAndReportAsync(ct), ct); }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { Log.Write("subscriptions round failed: " + ex.Message); }
            try { await Task.Delay(Every, ct); } catch (OperationCanceledException) { return; }
        }
    }

    private void Show()
    {
        foreach (var st in _m.Last)
        {
            if (!_lines.TryGetValue(st.Id, out var l)) continue;
            l.Text = st.Line() + (st.Path.Length > 0 ? "   " + st.Path : "");
            l.Foreground = st.LoggedIn == true ? Brushes.ForestGreen : st.Installed ? Brushes.DarkOrange : Brushes.DimGray;
        }
        foreach (var c in CliCatalog.All)
        {
            var s = _m.Login(c.Id);
            _logins[c.Id].Text = s == null ? "" : s.Describe();
        }
        _server.Text = _m.LastReport;
    }

    private async Task ConnectAsync(string id)
    {
        var c = CliCatalog.Get(id);
        var st = await Task.Run(() => _m.StatusAsync(id, CancellationToken.None));
        if (!st.Installed)
        {
            if (MessageBox.Show($"Install {c.Title} on this computer now? It takes a minute or two.", "Subscriptions",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            _logins[id].Text = "installing…";
            _logins[id].Text = await Task.Run(() => _m.InstallAsync(id, CancellationToken.None));
        }
        else if (st.LoggedIn == true && MessageBox.Show($"{c.Title} is already signed in{(st.Account.Length > 0 ? " as " + st.Account : "")}. Sign in again (another account)?",
                     "Subscriptions", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        string email = _email.Text.Trim();
        var s = await Task.Run(() => _m.StartLoginAsync(id, CancellationToken.None, email: email.Length > 0 ? email : null));
        _logins[id].Text = s.Describe();
    }

    private async Task CheckAsync(string id)
    {
        var st = await Task.Run(() => _m.StatusAsync(id, CancellationToken.None));
        if (st.LoggedIn != true) { MessageBox.Show(st.Line(), "Subscriptions"); return; }
        _logins[id].Text = "asking " + CliCatalog.Get(id).Title + " for one word…";
        try { _logins[id].Text = await Task.Run(() => _m.VerifyAsync(id, CancellationToken.None)); }
        catch (Exception ex) { _logins[id].Text = ex.Message; }
    }

    private static TextBlock H(string t) => new() { Text = t, FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 4) };
    private static TextBlock Text(string t) => new() { Text = t, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 2) };

    private static Button Button(string text, Func<Task> action)
    {
        var b = new Button { Content = text, Margin = new Thickness(0, 6, 6, 0), Padding = new Thickness(6, 3, 6, 3) };
        b.Click += async (_, _) =>
        {
            b.IsEnabled = false;
            try { await action(); }
            catch (Exception ex) { Log.Write("subscriptions " + text + ": " + ex); MessageBox.Show(ex.Message, "Subscriptions"); }
            finally { b.IsEnabled = true; }
        };
        return b;
    }
}

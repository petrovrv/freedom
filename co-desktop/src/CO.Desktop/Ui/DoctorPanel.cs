// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CO.Core.Doctor;

namespace CO.Desktop.Ui;

// S3: Doctor tab on the computer that runs Bridge — report, repairs, and a question to the local CLI agent.
// The doctor round runs every 5 minutes whether or not CO Desktop is signed in or paired.
public sealed class DoctorPanel : StackPanel
{
    private readonly BridgeDoctor _d;
    private readonly Action<string, string, bool> _notify;   // title, text, warning
    private readonly TextBlock _summary = new() { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 6, 0, 2) };
    private readonly TextBox _report = Mono(220);
    private readonly WrapPanel _fixes = new();
    private readonly TextBox _question = new() { TextWrapping = TextWrapping.Wrap, AcceptsReturn = true, Height = 50 };
    private readonly TextBox _answer = Mono(240);
    private DoctorLevel _shown = DoctorLevel.Ok;

    public static readonly TimeSpan Every = TimeSpan.FromMinutes(5);

    public DoctorPanel(BridgeDoctor doctor, Action<string, string, bool> notify)
    {
        _d = doctor;
        _notify = notify;
        Margin = new Thickness(10);
        Children.Add(H("Bridge doctor"));
        Children.Add(Text("Checks Bridge on this computer from its own files every 5 minutes, also when the site and Bridge are down. " +
            "Safe repairs run by themselves (start the launcher; restart a hung Bridge after its jobs). Reports go to Health on the site."));
        Children.Add(_summary);
        Children.Add(_report);
        Children.Add(Button("Check now", async () => Show(await Task.Run(() => _d.TickAsync(CancellationToken.None)))));
        Children.Add(Text("Repairs:"));
        Children.Add(_fixes);
        foreach (string id in new[] { DoctorFix.StartLauncher, DoctorFix.Restart, DoctorFix.Rollback, DoctorFix.CancelStuck, DoctorFix.HardRestart })
            _fixes.Children.Add(Button(DoctorFix.Label(id), id, () => RepairAsync(id)));

        Children.Add(H("Ask the local agent"));
        Children.Add(Text("Runs claude or codex of this computer directly with the report and the log tails. It only reads files; " +
            "repairs stay with the buttons above. Question (optional):"));
        Children.Add(_question);
        var row = new WrapPanel();
        row.Children.Add(Button("Ask Claude", () => AskAsync("claude")));
        row.Children.Add(Button("Ask Codex", () => AskAsync("codex")));
        Children.Add(row);
        Children.Add(_answer);
    }

    public async Task RunLoopAsync(CancellationToken ct)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(30), ct); } catch (OperationCanceledException) { return; }
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var r = await Task.Run(() => _d.TickAsync(ct), ct);
                await Dispatcher.InvokeAsync(() => Show(r));
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { Log.Write("doctor round failed: " + ex); }
            try { await Task.Delay(Every, ct); } catch (OperationCanceledException) { return; }
        }
    }

    private void Show(DoctorReport r)
    {
        _summary.Text = $"{r.Utc.ToLocalTime():HH:mm:ss}  {r.Summary}";
        _summary.Foreground = r.Level switch
        {
            DoctorLevel.Fail => Brushes.Firebrick,
            DoctorLevel.Warn => Brushes.DarkOrange,
            _ => Brushes.ForestGreen,
        };
        _report.Text = r.ToText();
        var fixes = r.Fixes.ToHashSet();
        foreach (Button b in _fixes.Children)
            b.FontWeight = fixes.Contains((string)b.Tag) ? FontWeights.Bold : FontWeights.Normal;
        if (r.Level == DoctorLevel.Fail && _shown != DoctorLevel.Fail)
            _notify("Bridge doctor", r.Summary, true);
        _shown = r.Level;
    }

    private async Task RepairAsync(string id)
    {
        var last = _d.Last;
        string confirm = id switch
        {
            DoctorFix.HardRestart => $"Kill Bridge now? Its running jobs ({last?.Owner?.RunningJobs.ToString() ?? "?"} by the last heartbeat) end at once.",
            DoctorFix.Rollback => "Roll back to the previous Bridge version? The current one is marked bad and is not installed again.",
            DoctorFix.CancelStuck => "Cancel the jobs that started over the timeout ago and wrote no events for 30 min? Bridge ends them if it still runs them.",
            _ => "",
        };
        if (confirm.Length > 0 && MessageBox.Show(confirm, "Bridge doctor", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        string outcome = await Task.Run(() => _d.RepairAsync(id, false, CancellationToken.None));
        MessageBox.Show(outcome, "Bridge doctor");
        Show(await Task.Run(() => _d.CheckAsync(CancellationToken.None)));
    }

    private async Task AskAsync(string agent)
    {
        string? exe = _d.ResolveCli(agent);
        if (exe == null) throw new InvalidOperationException(agent + " CLI is not installed on this computer.");
        _answer.Text = $"{agent} is thinking (up to 5 min)…";
        var r = _d.Last ?? await Task.Run(() => _d.CheckAsync(CancellationToken.None));
        string prompt = DoctorAi.Prompt(r, _d.Layout, _question.Text);
        try
        {
            string answer = await Task.Run(() => DoctorAi.AskAsync(agent, exe, prompt, _d.Layout, TimeSpan.FromMinutes(5), CancellationToken.None));
            var repairs = DoctorAi.Repairs(answer);
            _answer.Text = answer + (repairs.Count > 0 ? "\r\n\r\nSuggested: " + string.Join(", ", repairs.Select(DoctorFix.Label)) : "");
            foreach (Button b in _fixes.Children)
                if (repairs.Contains((string)b.Tag)) b.FontWeight = FontWeights.Bold;
        }
        catch (Exception ex) { _answer.Text = agent + ": " + ex.Message; }
    }

    private static TextBox Mono(double height) => new()
    {
        IsReadOnly = true, TextWrapping = TextWrapping.Wrap, Height = height, FontFamily = new FontFamily("Consolas"), FontSize = 11,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 4, 0, 4),
    };

    private static TextBlock H(string t) => new() { Text = t, FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 4) };
    private static TextBlock Text(string t) => new() { Text = t, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 2) };

    private static Button Button(string text, Func<Task> action)
    {
        var b = new Button { Content = text, Margin = new Thickness(0, 6, 6, 0), Padding = new Thickness(6, 3, 6, 3) };
        b.Click += async (_, _) =>
        {
            b.IsEnabled = false;
            try { await action(); }
            catch (Exception ex) { Log.Write(text + ": " + ex); MessageBox.Show(ex.Message, "Bridge doctor"); }
            finally { b.IsEnabled = true; }
        };
        return b;
    }

    private static Button Button(string text, string id, Func<Task> action)
    {
        var b = Button(text, action);
        b.Tag = id;
        return b;
    }
}

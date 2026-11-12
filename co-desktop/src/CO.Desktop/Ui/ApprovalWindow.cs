// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using CO.Core.Host;

namespace CO.Desktop.Ui;

// TZ §9.3: agent, Run, tool, full arguments, diff; Allow / Allow for this Task / Decline with comment.
public sealed class ApprovalWindow : Window
{
    private readonly TextBox _comment = new() { MinHeight = 48, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap };
    public ApprovalAnswer Answer { get; private set; } = new(ApprovalChoice.Deny, "closed");

    public ApprovalWindow(ApprovalRequest r)
    {
        Title = r.Kind == ApprovalKind.ReleaseResult ? "CO — release result to the model?" : $"CO — allow {r.Call.Tool}?";
        Width = 760;
        Height = 620;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var root = new DockPanel { Margin = new Thickness(12) };
        var header = new StackPanel();
        header.Children.Add(Line($"{r.Plan.Class} · {r.Call.Tool}", 18, FontWeights.SemiBold));
        header.Children.Add(Line($"Run {r.Call.RunId} · Task {r.Call.TaskId} · call {r.Call.CallId}", 12, FontWeights.Normal, Brushes.Gray));
        header.Children.Add(Line(r.Decision.Reason, 13, FontWeights.Normal));
        if (r.Tainted)
            header.Children.Add(Banner("This Run has read external content (email, web, files outside the project). " +
                                       "Check that the action is what you asked for.", Brushes.DarkOrange));
        if (r.Highlights.Count > 0)
            header.Children.Add(Banner("Came from external content: " + string.Join(", ", r.Highlights), Brushes.Firebrick));
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        var deny = Btn("Decline", () => Close(new ApprovalAnswer(ApprovalChoice.Deny, _comment.Text)));
        var allow = Btn("Allow", () => Close(new ApprovalAnswer(ApprovalChoice.Allow)));
        buttons.Children.Add(deny);
        if (r.Decision.Grantable && r.Kind == ApprovalKind.Action)
            buttons.Children.Add(Btn("Allow for this Task", () => Close(new ApprovalAnswer(ApprovalChoice.AllowForTask))));
        buttons.Children.Add(allow);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);

        var commentPanel = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        commentPanel.Children.Add(Line("Comment for the model (sent when you decline):", 12, FontWeights.Normal));
        commentPanel.Children.Add(_comment);
        DockPanel.SetDock(commentPanel, Dock.Bottom);
        root.Children.Add(commentPanel);

        var body = new TextBox
        {
            IsReadOnly = true, FontFamily = new FontFamily("Consolas"), FontSize = 12,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            Text = BuildBody(r),
        };
        root.Children.Add(body);
        Content = root;
        deny.IsDefault = false;
        allow.IsDefault = false;   // Enter never approves by accident
    }

    private static string BuildBody(ApprovalRequest r)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(r.Plan.Summary).AppendLine();
        sb.AppendLine("Arguments:");
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(r.Call.ArgsJson);
            sb.AppendLine(System.Text.Json.JsonSerializer.Serialize(doc.RootElement, new System.Text.Json.JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        }
        catch (System.Text.Json.JsonException) { sb.AppendLine(r.Call.ArgsJson); }
        if (r.Plan.Diff != null) sb.AppendLine().AppendLine("Change:").AppendLine(r.Plan.Diff);
        if (r.ResultPreview != null) sb.AppendLine().AppendLine("Result that would go to the model:").AppendLine(r.ResultPreview);
        return sb.ToString();
    }

    private void Close(ApprovalAnswer a)
    {
        Answer = a;
        Close();
    }

    private static TextBlock Line(string t, double size, FontWeight w, Brush? color = null) =>
        new() { Text = t, FontSize = size, FontWeight = w, Foreground = color ?? SystemColors.WindowTextBrush, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 2) };

    private static Border Banner(string t, Brush color) => new()
    {
        BorderBrush = color, BorderThickness = new Thickness(2), Padding = new Thickness(6), Margin = new Thickness(0, 4, 0, 4),
        Child = new TextBlock { Text = t, Foreground = color, TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold },
    };

    private static Button Btn(string text, Action a)
    {
        var b = new Button { Content = text, MinWidth = 120, Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(10, 4, 10, 4) };
        b.Click += (_, _) => a();
        return b;
    }
}

public sealed class WpfApprovalService : IApprovalService
{
    private readonly SemaphoreSlim _one = new(1, 1);   // one confirmation window at a time
    public event Action<ApprovalRequest>? Pending;

    public async Task<ApprovalAnswer> RequestAsync(ApprovalRequest request, CancellationToken ct)
    {
        await _one.WaitAsync(ct);
        try
        {
            Pending?.Invoke(request);
            return await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                var w = new ApprovalWindow(request);
                using var reg = ct.Register(() => w.Dispatcher.BeginInvoke(() => w.Close()));
                w.ShowDialog();
                return w.Answer;
            }).Task;
        }
        finally { _one.Release(); }
    }
}

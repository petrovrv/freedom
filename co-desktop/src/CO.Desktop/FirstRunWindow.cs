// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Windows;
using System.Windows.Controls;

namespace CO.Desktop;

// Shown once, when this computer has no stored Google sign-in.
public sealed class FirstRunWindow : Window
{
    public string? Environment { get; private set; }

    public FirstRunWindow(DesktopSettings settings)
    {
        Title = "CO Desktop — first start";
        Width = 460;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ResizeMode = ResizeMode.NoResize;
        Icon = AppIcon.Window();

        var p = new StackPanel { Margin = new Thickness(18) };
        p.Children.Add(new TextBlock { Text = "Who uses this computer?", FontSize = 16, FontWeight = FontWeights.SemiBold });
        var choice = new ComboBox { Margin = new Thickness(0, 10, 0, 0) };
        foreach (var (env, email) in settings.Members)
        {
            bool owner = email.Equals(settings.OwnerEmail, StringComparison.OrdinalIgnoreCase);
            choice.Items.Add(new ComboBoxItem { Tag = env, Content = $"{char.ToUpperInvariant(env[0])}{env[1..]} — {email}{(owner ? " (owner)" : "")}" });
        }
        choice.SelectedIndex = 0;
        p.Children.Add(choice);
        p.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0),
            Text = "Google opens in your browser once. CO Desktop keeps the sign-in in Windows Credential Manager, " +
                   "connects this computer to the home Bridge and signs in the CO window. " +
                   "On a member's computer the owner confirms the connection with a 6-digit code.",
        });
        var ok = new Button { Content = "Sign in with Google", Margin = new Thickness(0, 16, 0, 0), Padding = new Thickness(10, 6, 10, 6), IsDefault = true };
        ok.Click += (_, _) =>
        {
            Environment = (choice.SelectedItem as ComboBoxItem)?.Tag as string;
            DialogResult = true;
        };
        p.Children.Add(ok);
        Content = p;
    }
}

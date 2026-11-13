// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;

namespace CO.Desktop;

// Freedom 0.4.0: first start of a neutral build (release installer without a built-in site). Asks for what neither the
// build nor an install.json of this computer gives: the user's own CO site, the owner's Google account and the desktop
// OAuth client of the user's Google project (install/GOOGLE-CLOUD.md step 5). A build with install.json never shows it.
public sealed class SetupWindow : Window
{
    private readonly DesktopSettings _settings;
    private readonly TextBox _site = new(), _email = new(), _name = new(), _client = new();
    private readonly TextBlock _error = new() { Foreground = System.Windows.Media.Brushes.Firebrick, TextWrapping = TextWrapping.Wrap };

    public static bool Needed(DesktopSettings s) =>
        !s.HasSite || s.Members.Count == 0 || s.OwnerEmail.Length == 0 || !File.Exists(s.ResolveOAuthClientFile());

    public SetupWindow(DesktopSettings settings)
    {
        _settings = settings;
        Title = "CO Desktop — connect to your site";
        Width = 520;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ResizeMode = ResizeMode.NoResize;
        Icon = AppIcon.Window();

        var p = new StackPanel { Margin = new Thickness(18) };
        p.Children.Add(new TextBlock { Text = "Connect CO Desktop to your own site", FontSize = 16, FontWeight = FontWeights.SemiBold });
        p.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0),
            Text = "This copy of CO Desktop has no site built in. Enter the site you installed with the setup wizard " +
                   "(install/PILOT-FRIEND.md). On a household member's computer copy install.json and " +
                   "oauth-client-desktop.json from the owner's computer to %LOCALAPPDATA%\\AIWorkbench instead and start again.",
        });
        _site.Text = settings.WebUrl.TrimEnd('/');
        _email.Text = settings.OwnerEmail;
        _name.Text = settings.Members.Count > 0 ? settings.EnvironmentId : "";
        string client = settings.ResolveOAuthClientFile();
        _client.Text = File.Exists(client) ? client : "";
        Field(p, "Site address (https://co.example.org)", _site);
        Field(p, "Your Google account (the owner of the site)", _email);
        Field(p, "Your first name (latin letters; becomes your environment id)", _name);
        Field(p, "Desktop OAuth client of your Google project (JSON with an \"installed\" section)", _client);
        var browse = new Button { Content = "Choose file…", Margin = new Thickness(0, 4, 0, 0), Padding = new Thickness(8, 3, 8, 3), HorizontalAlignment = HorizontalAlignment.Left };
        browse.Click += (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Desktop OAuth client (client_secret_….json)", Filter = "JSON (*.json)|*.json" };
            if (dlg.ShowDialog(this) == true) _client.Text = dlg.FileName;
        };
        p.Children.Add(browse);
        p.Children.Add(_error);
        var ok = new Button { Content = "Save and continue", Margin = new Thickness(0, 14, 0, 0), Padding = new Thickness(10, 6, 10, 6), IsDefault = true };
        ok.Click += (_, _) => Save();
        p.Children.Add(ok);
        Content = p;
    }

    private static void Field(StackPanel p, string label, TextBox box)
    {
        p.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 10, 0, 2) });
        p.Children.Add(box);
    }

    /// <summary>Environment id from a first name, the rule of freedom_setup.py init ("owner" when nothing is left).</summary>
    public static string EnvFromName(string name)
    {
        string env = Regex.Replace(name.Trim().ToLowerInvariant(), "[^a-z0-9-]", "");
        return env.Length is > 0 and <= 40 ? env : "owner";
    }

    /// <summary>"" when the address may be a CO site: https://host, or http://localhost:PORT (variant C without Tailscale).</summary>
    public static string CheckSite(string url) =>
        Regex.IsMatch(url, @"\Ahttps://[a-z0-9.-]+(:\d+)?\z", RegexOptions.IgnoreCase) || Regex.IsMatch(url, @"\Ahttp://localhost:\d+\z", RegexOptions.IgnoreCase)
            ? "" : "The site address must look like https://co.example.org (no path, no trailing slash).";

    /// <summary>"" when the file is a desktop ("installed") OAuth client.</summary>
    public static string CheckClient(string json)
    {
        try
        {
            JsonNode? c = JsonNode.Parse(json)?["installed"];
            return c?["client_id"]?.GetValue<string>() is { Length: > 0 } && c["client_secret"]?.GetValue<string>() is { Length: > 0 }
                ? "" : "This is not a desktop OAuth client (it needs an \"installed\" section; a \"web\" client is for the site).";
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException)
        {
            return "The OAuth client file is not valid JSON.";
        }
    }

    private void Save()
    {
        string site = _site.Text.Trim().TrimEnd('/'), email = _email.Text.Trim().ToLowerInvariant(), file = _client.Text.Trim().Trim('"');
        string err = CheckSite(site);
        if (err.Length == 0 && !Regex.IsMatch(email, @"\A[^@\s]+@[^@\s]+\.[^@\s]+\z")) err = "Enter the Google account (e-mail) of the owner.";
        if (err.Length == 0 && _name.Text.Trim().Length == 0) err = "Enter your first name.";
        string json = "";
        if (err.Length == 0)
        {
            try { json = File.ReadAllText(file); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { err = "Choose the desktop OAuth client file."; }
        }
        if (err.Length == 0) err = CheckClient(json);
        if (err.Length > 0) { _error.Text = err; return; }

        string target = DesktopSettings.WizardOAuthClientFile;
        if (!string.Equals(Path.GetFullPath(file), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, json);
        }
        string env = _settings.Members.FirstOrDefault(kv => kv.Value.Equals(email, StringComparison.OrdinalIgnoreCase)).Key ?? EnvFromName(_name.Text);
        _settings.WebUrl = site + "/";
        _settings.OwnerEmail = email;
        _settings.EnvironmentId = env;
        if (!_settings.Members.ContainsKey(env)) _settings.Members = new Dictionary<string, string>(_settings.Members) { [env] = email };
        _settings.OAuthClientFile = "";
        _settings.Save();
        DialogResult = true;
    }
}

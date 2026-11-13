// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Windows;
using System.Windows.Controls;

namespace CO.Corporate;

// Settings panels of the corporate module on the Connectors tab of CO Desktop (moved from MainWindow, Freedom F1):
// adding a SharePoint / OneDrive folder by link and the Okta sign-in fallback.
internal static class CorporatePanels
{
    public static UIElement FolderByLink(ConnectorModuleContext ctx)
    {
        var p = new StackPanel();
        p.Children.Add(H("Add a SharePoint or OneDrive folder"));
        p.Children.Add(Label("Address of a folder or library from the browser (any SharePoint site or your OneDrive); no sync needed. " +
            "Sign in once in the CO browser. Each one becomes its own row; the name is how the agent calls it."));
        var url = new TextBox();
        p.Children.Add(url);
        p.Children.Add(Label("Name (optional; a-z, 0-9, '-', ≤ 12; made from the folder name when empty)"));
        var id = new TextBox();
        p.Children.Add(id);
        p.Children.Add(Btn("Add folder", ctx.Log, async () =>
        {
            var sp = (ctx.Folders.RemoteFolders as SharePointFs)?.Service ?? throw new InvalidOperationException("SharePoint is not available.");
            var path = await SharePointPath.ResolveAsync(ctx.Browser, url.Text, CancellationToken.None);
            if (await sp.StatAsync(path, CancellationToken.None) is not { IsDir: true })
                throw new InvalidOperationException("This link is not a folder you can open.");
            ctx.Folders.AddRemote(path, ctx.Folders.MakeId(id.Text, SharePointNames.IdSource(path.Url)), Rights.Read);
            id.Text = "";
            url.Text = "";
        }));
        return p;
    }

    // One Okta fallback for every connector that signs in through Okta in the CO browser.
    public static UIElement OktaBox(SharePointService? sp0, Action<string> log)
    {
        var p = new StackPanel();
        var box = new GroupBox
        {
            Header = new TextBlock { Text = "Okta sign-in fallback (optional)", FontWeight = FontWeights.SemiBold, FontSize = 14 },
            Content = p, Margin = new Thickness(0, 14, 0, 0), Padding = new Thickness(6, 2, 6, 6),
        };
        p.Children.Add(Label("Used by SharePoint and OneDrive (later Tractebel Portal and Teams). Order of sign-in: CO browser session → " +
            "Windows logon (in the office network Okta usually needs nothing more) → CO browser sign-in → this password. The password " +
            "stays in Windows Credential Manager on this computer; away from the office Okta still sends a push to Okta Verify."));
        // 0.8.2: default the Okta address to ENGIE's so engie.ai sign-in works out of the box; the user can change it.
        var oktaUrl = new TextBox { Text = string.IsNullOrEmpty(sp0?.Okta.OktaUrl) ? "https://engie.okta-emea.com" : sp0!.Okta.OktaUrl };
        var appLink = new TextBox { Text = sp0?.Okta.AppLink ?? "" };
        var user = new TextBox { Text = sp0?.Okta.UserName ?? "" };
        var pass = new PasswordBox();
        var oktaState = Label(sp0?.HasPassword == true ? "Password stored." : "No password stored.");
        p.Children.Add(Label("Okta address (e.g. https://engie.okta-emea.com)"));
        p.Children.Add(oktaUrl);
        p.Children.Add(Label("Okta app link of Microsoft 365 / SharePoint (optional; from the Okta dashboard tile)"));
        p.Children.Add(appLink);
        p.Children.Add(Label("User name"));
        p.Children.Add(user);
        p.Children.Add(Label("Password (leave empty to keep the stored one)"));
        p.Children.Add(pass);
        p.Children.Add(oktaState);
        p.Children.Add(Btn("Save Okta fallback", log, () =>
        {
            var sp = sp0 ?? throw new InvalidOperationException("SharePoint is not available.");
            sp.SaveOkta(new OktaFallback { OktaUrl = oktaUrl.Text.Trim(), AppLink = appLink.Text.Trim(), UserName = user.Text.Trim() },
                pass.Password.Length > 0 ? pass.Password : null);
            pass.Clear();
            oktaState.Text = sp.HasPassword ? "Saved; password stored." : "Saved; no password stored.";
            return Task.CompletedTask;
        }));
        p.Children.Add(Btn("Remove stored password", log, () =>
        {
            if (sp0 == null) return Task.CompletedTask;
            sp0.SaveOkta(sp0.Okta, "");
            oktaState.Text = "No password stored.";
            return Task.CompletedTask;
        }));
        return box;
    }

    // Same look as the panels of CO Desktop.
    private static TextBlock H(string t) => new() { Text = t, FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 4) };
    private static TextBlock Label(string t) => new() { Text = t, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 2) };

    private static Button Btn(string text, Action<string> log, Func<Task> action)
    {
        var b = new Button { Content = text, Margin = new Thickness(0, 6, 0, 0), Padding = new Thickness(6, 3, 6, 3), HorizontalContentAlignment = HorizontalAlignment.Left };
        b.Click += async (_, _) =>
        {
            b.IsEnabled = false;
            try { await action(); }
            catch (Exception ex) { log(text + ": " + ex); MessageBox.Show(ex.Message, "CO Desktop"); }
            finally { b.IsEnabled = true; }
        };
        return b;
    }
}

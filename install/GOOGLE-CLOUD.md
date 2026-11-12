# Your own Google Cloud project (SPEC Freedom §4.1 step 5)

A playbook for **your own agent** (Claude Code or Codex signed in to your own subscription, step 3 of the wizard).
The agent works in the visible browser of your computer (browser-agent, or the browser of CO Desktop). Wherever
Google asks for your account, a password, a second factor or an "I agree", **you** click; the agent waits, then
checks the result. The agent never types passwords and never sees the client secret: the downloaded file goes
straight into the OS keyring through the wizard. Target: 20 minutes.

Say to the agent: *"Follow install/GOOGLE-CLOUD.md step by step in my browser; stop where my account is needed."*

## What you get

| Piece | Used by | Where it ends up |
|---|---|---|
| Project (id and number) | everything | `install.json` `google.projectNumber`; Bridge `config.json` `google.projectId` |
| Google Drive API, Cloud Pub/Sub API | CO Web, Bridge, site | enabled in the project |
| OAuth consent screen, External, **In production** | sign-in | in the project (Testing would end every sign-in after 7 days) |
| OAuth client "Web application" | sign-in of CO Web | id → `install.json` `google.webClientId`; secret → keyring `aiwb/google-oauth-web` → `~/co-auth/config.php` on the hosting |
| OAuth client "Desktop app" | Bridge (Connect Google) | `%LOCALAPPDATA%\AIWorkbench\oauth-client-desktop.json` (macOS: `~/.config/aiwb/oauth-client-desktop.json`) |
| Pub/Sub topics and subscriptions | Bridge, CO Web | **made by Bridge itself** on its first start (`co-<host>-jobs`, `co-<host>-events-<env>` …); nothing to do by hand |

## Steps

1. **Project.** https://console.cloud.google.com/projectcreate — name `freedom-<your first name>`, no organisation.
   *You:* sign in, accept the terms if asked. *Agent:* opens the project dashboard and reads the **project number** and
   the **project id**.
2. **APIs.** Open and press *Enable* on
   https://console.cloud.google.com/apis/library/drive.googleapis.com and
   https://console.cloud.google.com/apis/library/pubsub.googleapis.com (with `?project=<id>`).
   *Agent:* checks that both show *API enabled*.
3. **Consent screen.** https://console.cloud.google.com/auth/overview → *Get started*: app name `Freedom`, support
   e-mail = yours, audience **External**, contact e-mail = yours, agree to the policy (*you*). Then *Data access* → add
   scopes `openid`, `.../auth/userinfo.email`, `.../auth/drive`, `.../auth/pubsub`. Then *Audience* → **Publish app**
   (status *In production*). Google does not review a personal app with fewer than 100 users; at the first sign-in you
   see "Google hasn't verified this app" → *Advanced* → *Go to Freedom* — that is your own app.
   Members of your household who sign in later see the same warning.
4. **Web client.** https://console.cloud.google.com/auth/clients → *Create client* → *Web application*, name
   `Freedom web`:
   - Authorized JavaScript origins: `https://<your site>` (the `site.url` of install.json, no slash at the end);
   - Authorized redirect URIs: `https://<your site>/auth/callback.php`.
   *Create* → *Download JSON* (*you* press it; the file lands in Downloads). Then, on your computer:

   ```
   python install/freedom_setup.py google --import-web-client "%USERPROFILE%\Downloads\client_secret_….json"
   ```

   The wizard puts the secret into the keyring, the id and the project number into install.json. Delete the downloaded
   file afterwards.
5. **Desktop client.** Same page → *Create client* → *Desktop app*, name `Freedom bridge` → *Download JSON* → save it as
   `%LOCALAPPDATA%\AIWorkbench\oauth-client-desktop.json`. Bridge: tray icon → *Connect Google (Pub/Sub + Drive API)...* → choose that file →
   sign in (*you*). Put the project id into Bridge `config.json` → `google.projectId`.
6. **Check.**

   ```
   python install/freedom_setup.py google                       # everything ok?
   python install/freedom_setup.py site-deploy --apply --parts config    # ~/co-auth/config.php on the hosting
   python install/freedom_setup.py verify
   ```

   Then open your site and sign in. The light in the header turns blue (the site works) and green once Bridge is
   connected (Pub/Sub state `pull` in the Bridge tray).

## When something goes wrong

| Symptom | Cause | Fix |
|---|---|---|
| `redirect_uri_mismatch` at sign-in | the redirect URI of step 4 differs | exactly `https://<site>/auth/callback.php` |
| `access_denied`, "app is in testing" | step 3 not published | *Audience* → *Publish app* |
| Sign-in works, after a week it stops | consent screen still *Testing* | publish; sign in again |
| Bridge: Pub/Sub `403 PERMISSION_DENIED` | API not enabled or another project id in config.json | step 2; `google.projectId` |
| `google` says the project number does not match | the web client is from another project | create it in this project |

Stuck: `python install/freedom_setup.py report` and paste the report into your own claude.ai / chatgpt.com chat or the
Freedom forum. The report has no names, e-mails, hosts or secrets.

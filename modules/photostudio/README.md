# photostudio — PhotoStudio as a device app (ext/1, SPEC §3.6a F14d)

PhotoStudio (open-source photo editor, MIT; C#/.NET + Avalonia, LibRaw, ONNX) is a program of the computer, not a
web page. This module makes it an app of the workbench without rebuilding CO Desktop:

- **App** 🖼 in the launcher ▦ (`ui/app.html`). Inside CO Desktop the page calls the device op `ps_status`, then
  `ps_open` starts PhotoStudio on that computer; in a plain browser or on a phone the page says where it runs.
- **Open with**: "Open in PhotoStudio" for jpg, jpeg, png, tif, tiff, cr2 files of chats opens the app page with
  `?path=projects/...`; the page opens that photo in the program.
- **Device tools** for agents (any chat, also from a phone while the computer is on):
  `photostudio_open {files}`, `photostudio_info {file}`, `photostudio_enhance {file, out?, agent?}` (AI plan by the
  person's own Claude/Codex CLI) and `photostudio_idphoto {file, outDir?, spec?, sheet?}`. Paths are absolute or
  relative to AI-Workbench (`projects/...`, resolved through install.json).

All of it runs `device/photostudio.py` (python -I) on the device, sha256-checked by CO Desktop before each run.

## Where the program is found

1. `config.json` in the module's state folder (`%LOCALAPPDATA%\CO\modules\photostudio\state\config.json` on CO
   Desktop): `{"app": "D:\Apps\PhotoStudio\PhotoStudio.exe", "cli": "D:\Apps\PhotoStudio\photostudio-cli.exe"}`
2. `PHOTOSTUDIO_APP` / `PHOTOSTUDIO_CLI`
3. `%LOCALAPPDATA%\Programs\PhotoStudio` (cli next to the app or in `cli\`)
4. PATH

`photostudio-cli.exe` is built from the same repository: `dotnet publish src/PhotoStudio.Cli -c Release -o <folder>`.

## Tests

`python modules/photostudio/tests/test_device.py` (protocol and path rules, no install needed); `--live` also asks
the real install named by `PHOTOSTUDIO_APP` / `PHOTOSTUDIO_CLI` (`PHOTOSTUDIO_TEST_IMAGE` for `info`).

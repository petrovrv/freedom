# SPDX-License-Identifier: AGPL-3.0-or-later
# Freedom bootstrap for Windows (SPEC 4.1 step 2, F2b): the only part of the installation without AI.
#
#   powershell -ExecutionPolicy Bypass -c "& ([scriptblock]::Create((irm <release>/freedom-bootstrap.ps1))) -Invite fdm-inv1.XXX"
#
# 1. installs ONE agent CLI with its official installer (Claude Code, or Codex for a ChatGPT subscription) and what
#    it needs (Git for Windows / Node LTS through winget);
# 2. downloads the release (sources, CO Desktop installer, manifest) into %USERPROFILE%\Freedom, checks the sha256
#    of every file against the manifest and unpacks the sources;
# 3. the human signs in to the own paid subscription (the last click is always the human's, SPEC 11.3);
# 4. starts that agent in the sources folder with install/AGENT-INSTALL.md: from here the own AI installs the rest
#    (Python, packages, the wizard steps) and calls the human only for sign-ins, payments, secrets and "yes".
# Run it again at any time: finished parts are skipped and the agent continues where it stopped.
param(
    [string]$Invite = '',
    [ValidateSet('', 'claude', 'codex')][string]$Cli = '',
    [string]$Version = 'latest',
    [string]$Dir = (Join-Path $env:USERPROFILE 'Freedom'),
    [string]$Repo = '__FREEDOM_REPO__',   # owner/name on GitHub, filled in by release/build_installers.py
    [string]$Source = '',     # a local folder with the release files instead of GitHub (tests, offline)
    [string]$Lang = '',       # language the agent talks in; default: the Windows display language
    [switch]$NoAgent          # stop after the sign-in, do not start the agent
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

function Say([string]$t) { Write-Host "[freedom] $t" -ForegroundColor Cyan }
function Warn([string]$t) { Write-Host "[freedom] $t" -ForegroundColor Yellow }
function Have([string]$c) { [bool](Get-Command $c -ErrorAction SilentlyContinue) }
function Refresh-Path {
    $p = @([Environment]::GetEnvironmentVariable('Path', 'Machine'), [Environment]::GetEnvironmentVariable('Path', 'User'),
        (Join-Path $env:USERPROFILE '.local\bin'), (Join-Path $env:APPDATA 'npm'))
    $env:Path = ($p | Where-Object { $_ }) -join ';'
}
function Quiet([scriptblock]$b) {   # Windows PowerShell 5.1 turns stderr of a native command into an error under Stop
    $old = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
    try { & $b 2>&1 | Out-String } finally { $ErrorActionPreference = $old }
}
function Winget-Install([string]$id) {
    if (-not (Have winget)) { throw 'winget is missing: install "App Installer" from the Microsoft Store, then run this again' }
    Say "installing $id (winget)"
    winget install --id $id -e --silent --accept-package-agreements --accept-source-agreements | Out-Host
    Refresh-Path
}

$stateDir = Join-Path $env:LOCALAPPDATA 'AIWorkbench'
$statePath = Join-Path $stateDir 'freedom-bootstrap.json'
New-Item -ItemType Directory -Force $stateDir, $Dir | Out-Null
$state = @{}
if (Test-Path $statePath) {
    (Get-Content $statePath -Raw | ConvertFrom-Json).psobject.Properties | ForEach-Object { $state[$_.Name] = $_.Value }
}
function Save-State { $state['updatedUtc'] = (Get-Date).ToUniversalTime().ToString('s') + 'Z'
    $state | ConvertTo-Json | Set-Content -Encoding UTF8 $statePath }

# ---- questions (asked once, kept in the state file)
if (-not $Cli) { $Cli = [string]$state['cli'] }
if (-not $Cli) {
    Write-Host ''
    Write-Host 'Which paid subscription do you have?'
    Write-Host '  1  Claude Pro / Max (claude.ai)'
    Write-Host '  2  ChatGPT Plus / Pro (chatgpt.com)'
    $Cli = if ((Read-Host 'Choose 1 or 2 [1]') -eq '2') { 'codex' } else { 'claude' }
}
$state['cli'] = $Cli
if (-not $Invite -and -not $state.ContainsKey('invite')) {
    $Invite = (Read-Host 'Invite code from the person who invited you (Enter if nobody did)').Trim()
}
if ($Invite) { $state['invite'] = $Invite } elseif (-not $state.ContainsKey('invite')) { $state['invite'] = '' }
if (-not $Lang) { $Lang = (Get-UICulture).Name }
$state['lang'] = $Lang
Save-State

# ---- 1. the agent CLI and what it needs
Refresh-Path
if (-not (Have git)) { Winget-Install 'Git.Git' }   # Claude Code on Windows runs its shell in Git Bash
if ($Cli -eq 'claude') {
    if (-not (Have claude)) {
        Say 'installing Claude Code (official installer claude.ai/install.ps1)'
        & powershell -NoProfile -ExecutionPolicy Bypass -Command 'irm https://claude.ai/install.ps1 | iex' | Out-Host
        $bin = Join-Path $env:USERPROFILE '.local\bin'
        $userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
        if (($userPath -split ';') -notcontains $bin) { [Environment]::SetEnvironmentVariable('Path', "$userPath;$bin", 'User') }
        Refresh-Path
    }
    if (-not (Have claude)) { throw 'Claude Code did not install; see https://docs.claude.com/en/docs/claude-code/setup' }
} else {
    if (-not (Have npm)) { Winget-Install 'OpenJS.NodeJS.LTS' }
    if (-not (Have codex)) { Say 'installing Codex CLI (npm @openai/codex)'; & npm install -g '@openai/codex' | Out-Host; Refresh-Path }
    if (-not (Have codex)) { throw 'Codex CLI did not install (npm install -g @openai/codex)' }
}
Say "$Cli CLI: ok"

# ---- 2. the release: download, check sha256 against the manifest, unpack
$dl = Join-Path $Dir 'downloads'
New-Item -ItemType Directory -Force $dl | Out-Null
if ($Source) {
    Get-ChildItem $Source -File | Where-Object { $_.Name -match '^(freedom-.*-src\.tar\.gz|freedom-manifest\.json|CO-Desktop-Setup-.*\.exe)$' } |
        ForEach-Object { Copy-Item $_.FullName $dl -Force }
} else {
    if ($Repo -like '__*') { throw 'this copy of bootstrap.ps1 does not know its repository: pass -Repo <owner>/<name>' }
    $api = if ($Version -eq 'latest') { "https://api.github.com/repos/$Repo/releases/latest" } else { "https://api.github.com/repos/$Repo/releases/tags/v$Version" }
    $rel = Invoke-RestMethod $api -Headers @{ 'User-Agent' = 'freedom-bootstrap' }
    foreach ($as in $rel.assets) {
        if ($as.name -notmatch '^(freedom-.*-src\.tar\.gz|freedom-manifest\.json|CO-Desktop-Setup-.*\.exe)$') { continue }
        $to = Join-Path $dl $as.name
        if ((Test-Path $to) -and (Get-Item $to).Length -eq $as.size) { continue }
        Say "downloading $($as.name)"
        Invoke-WebRequest $as.browser_download_url -OutFile $to -UseBasicParsing
    }
}
$manifest = Get-Content (Join-Path $dl 'freedom-manifest.json') -Raw | ConvertFrom-Json
foreach ($f in $manifest.files) {
    $p = Join-Path $dl $f.path
    if (-not (Test-Path $p)) { continue }   # Linux Bridge builds are not needed on Windows
    $h = (Get-FileHash $p -Algorithm SHA256).Hash.ToLower()
    if ($h -ne $f.sha256) { Remove-Item $p; throw "$($f.path): sha256 does not match the manifest (file removed, run again)" }
}
$tarball = Get-ChildItem $dl -Filter 'freedom-*-src.tar.gz' | Sort-Object LastWriteTime | Select-Object -Last 1
if (-not $tarball) { throw 'no freedom-*-src.tar.gz in the release' }
$src = Join-Path $Dir ($tarball.Name -replace '-src\.tar\.gz$', '')
if (-not (Test-Path (Join-Path $src 'install\AGENT-INSTALL.md'))) {
    Say "unpacking $($tarball.Name)"
    & tar -xf $tarball.FullName -C $Dir
    if ($LASTEXITCODE -ne 0) { throw 'tar could not unpack the sources' }
}
$state['version'] = $manifest.version
$state['sources'] = $src
$state['downloads'] = $dl
$state['manifestChecked'] = 'sha256 (signatures: freedom_release.py verify, step 1 of AGENT-INSTALL.md)'
Save-State
Say "Freedom $($manifest.version): $src"

# ---- 3. sign-in to the own subscription (the human does the last click)
if ($Cli -eq 'claude') {
    $st = Quiet { claude auth status }
    if ($LASTEXITCODE -ne 0 -or $st -match 'not (logged|signed) in|"loggedIn":\s*false') {
        Say 'sign in to your Claude subscription: a browser window opens, finish it there (captcha, Authorize)'
        & claude auth login --claudeai | Out-Host
    }
} else {
    Quiet { codex login status } | Out-Null
    if ($LASTEXITCODE -ne 0) {
        Say 'sign in to your ChatGPT subscription: confirm the code shown below in your ChatGPT'
        & codex login --device-auth | Out-Host
    }
}
$state['signedIn'] = $true
Save-State
if ($NoAgent) { Say 'done (no agent started: -NoAgent)'; return }

# ---- 4. the own AI takes over
$prompt = "Install Freedom on this computer. Follow install/AGENT-INSTALL.md from the step where it stopped; " +
    "the bootstrap state is $statePath. Talk to me in the language $Lang. Start with a short plan."
Set-Location $src
Say "starting $Cli in $src - from here your own AI leads; answer it in the chat"
if ($Cli -eq 'claude') {
    & claude --settings (Join-Path $src 'install\agent\claude-settings.json') $prompt
} else {
    & codex $prompt
}

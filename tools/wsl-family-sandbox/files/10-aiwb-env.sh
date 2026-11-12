# AI Workbench family sandbox: per-user tool locations (no root needed)
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
export NPM_CONFIG_PREFIX="$HOME/.local" NPM_CONFIG_UPDATE_NOTIFIER=false
case ":$PATH:" in *":$HOME/.local/bin:"*) ;; *) PATH="$HOME/.local/bin:$HOME/.cargo/bin:$HOME/.dotnet/tools:$PATH" ;; esac
export PATH

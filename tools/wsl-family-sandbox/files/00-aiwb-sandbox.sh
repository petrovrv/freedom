# AI Workbench family sandbox (fallback; the login shell aiwb-shell already enters it):
# a family user's login shell that is not inside the sandbox re-executes itself through aiwb-enter.
if [ -r /etc/aiwb-family/users ] && awk -v u="$(id -un)" '$1==u{f=1} END{exit !f}' /etc/aiwb-family/users; then
    if ! grep -q '^NoNewPrivs:[[:space:]]*1' /proc/self/status; then
        if [ -n "${BASH_EXECUTION_STRING:-}" ]; then
            exec /usr/local/libexec/aiwb-enter /bin/bash -lc "$BASH_EXECUTION_STRING"
        else
            exec /usr/local/libexec/aiwb-enter /bin/bash -l
        fi
        exit 126
    fi
fi

#!/bin/bash
# SPDX-License-Identifier: AGPL-3.0-or-later
# CI-equivalent build of the exported freedom tree (outputs outside the tree, own build root)
E=${1:-$(cygpath -u "$LOCALAPPDATA")/AIWorkbenchWork/freedom-export}
B=$(cygpath -u "$LOCALAPPDATA")/AIWorkbenchBuild/freedom-export
mkdir -p $B
echo "== bridge build.cmd"; cmd.exe //c "$(cygpath -w $E/bridge/build.cmd) $(cygpath -w $B/bridge)" 2>&1 | tail -3
echo "== dotnet test CO.Core"; dotnet test $E/co-desktop/tests/CO.Core.Tests -c Release -p:AiwbBuildRoot=$(cygpath -w $B/cod)\ 2>&1 | tail -3
for t in $E/modules/*/tests/*/*.Tests.csproj; do [ -e "$t" ] || continue; echo "== dotnet test ${t#$E/}"; dotnet test $t -c Release -p:AiwbBuildRoot=$(cygpath -w $B/cod)\ 2>&1 | tail -3; done
echo "== dotnet build CO.Desktop"; dotnet build $E/co-desktop/src/CO.Desktop -c Release -p:AiwbBuildRoot=$(cygpath -w $B/cod)\ 2>&1 | tail -3
echo "== co-web npm ci + build"; cd $E/co-web && npm ci --no-audit --no-fund 2>&1 | tail -2 && npm run build 2>&1 | tail -4
echo "== php -l (php:8.1-cli in WSL docker)"; cd /c && wsl.exe -e bash -lc "cd $(wslpath -a "$(cygpath -w $E)" 2>/dev/null || echo /mnt/c${E#/c}) && docker run --rm --cpus 2 -v \$PWD:/src -w /src php:8.1-cli sh -c \"find co-agent co-web-auth -name '*.php' -print0 | xargs -0 -n1 php -l | grep -v '^No syntax errors' ; echo php-lint-done\"" 2>&1 | tr -d '\0' | tail -5
echo "== done"

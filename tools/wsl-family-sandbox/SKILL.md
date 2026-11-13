---
name: wsl-family-sandbox
description: Children's WSL2 sandbox aiwb-family on the home PC (Ubuntu 24.04) — household members (install.json members) compile and run code (C/C++, Python, Node LTS, .NET 8, Java 21, Rust) without seeing the owner's files, Windows drives, other distros, LAN or tailnet; cgroup limits on CPU/memory/processes, disk image per user, own netns with internet only, auto-clean of /tmp. Commands create / status / add-user / reset-user / del-user / check / run / destroy. Use to (re)build the sandbox or to run a child's command (the Bridge pc_* interface).
version: 0.1.4
entry: wsl-family-sandbox.py
requires: [WSL 2.6+ (wsl --install --name/--location/--vhd-size), internet for apt/nodejs.org on create]
secrets: []
allowed_hosts: [nodejs.org, static.rust-lang.org, pypi.org, registry.npmjs.org]
---

# wsl-family-sandbox

Отдельный дистрибутив WSL2 `aiwb-family` (`C:\AIWorkbench\wsl\aiwb-family`, диск до 40 ГБ), где члены семьи через Bridge компилируют и запускают код. Изнутри не видно ни файлов Windows и Drive, ни соседних дистрибутивов (Ubuntu с CoreScoping), ни домашней сети и tailnet.

## Интерфейс для Bridge (pc_*)

```
wsl.exe -d aiwb-family -u <user> --cd ~ -- bash -lc "<cmd>"
```

- `<user>` — член домохозяйства (env из `members` install.json, по умолчанию `AIWB_SANDBOX_USERS` или все не-владельцы) (строки `/etc/aiwb-family/users`). Аргументы после `--` WSL передаёт как есть: `"<cmd>"` доходит до `bash -c` одной строкой, кавычки внутри сохраняются (проверено: `printf "[%s] " "a b" c` → `[a b] [c]`). Надёжнее всего передавать скрипт в base64: `bash -lc "echo <b64> | base64 -d | bash"` (так делает `run`).
- Холодный старт дистрибутива ~1,5 с (загрузочный скрипт сам поднимает cgroup, netns, firewall); WSL гасит дистрибутив через несколько секунд простоя — это нормально, `$HOME` и `/tmp` пользователя лежат на его образе диска и сохраняются.
- Код выхода — код команды; 126 и строка `aiwb-enter: …` — песочница не поднялась (команда не выполнялась). Вывод — обычный stdout/stderr; WSL иногда пишет в stderr `your 131072x1 screen size is bogus` — игнорировать.
- Не запускать от `-u root` и не давать ребёнку выбирать `-u`: root дистрибутива видит общий для VM cgroup и сеть.
- То же из Python: `python wsl-family-sandbox.py run boris 'gcc hello.c && ./a.out'`.

## Как устроено

- **Вход**: оболочка входа детей — `/usr/local/bin/aiwb-shell` → setuid-помощник `/usr/local/libexec/aiwb-enter` (root:aiwbfam 4750, исходник `files/aiwb-enter.c`). Он кладёт процесс в cgroup `/sys/fs/cgroup/aiwb-family/<user>`, в сеть `aiwb-<user>`, создаёт свои mount- и cgroup-пространства (снимает `/mnt/*`, `/mnt/wsl`, `/mnt/wslg`, `/usr/lib/wsl`, X11-сокет WSLg; пустой tmpfs на `/mnt` и `/run/WSL`; `/proc` с `hidepid=invisible`; `/tmp` и `/var/tmp` = `$HOME/.tmp`; свой `/dev/shm` 256 МБ; `XDG_RUNTIME_DIR=/tmp/.run`), окончательно сбрасывает root, ставит `no_new_privs` (sudo/su/setuid не работают) и seccomp (запрет user namespaces, bpf, perf, userfaultfd; clone3/io_uring → ENOSYS). Запасные пути: `/etc/profile.d/00-aiwb-sandbox.sh` (для `-e bash -l`) и сторож `aiwb-family-watchdog`, который раз в 2 с убивает любой процесс ребёнка вне его cgroup/netns (например `wsl -u <user> -e prog`).
- **Wsl.conf**: `systemd=false` (cgroup-дерево VM общее с Ubuntu, второй systemd мог бы задеть её сервисы), automount off (нет `/mnt/c`…), interop off и `appendWindowsPath=false` (`cmd.exe`, `powershell.exe`, любой PE — отказ), свои `resolv.conf` и `hosts` (без поисковых доменов tailnet), пользователь по умолчанию `aiwbsvc` (uid 2999, `nologin`).
- **Сеть**: на пользователя свой netns с veth `afh<uid>` (10.231.N.0/30) и NAT. В общем сетевом пространстве VM — таблицы nft `inet aiwb_family` и `ip aiwb_family_nat`: из `afh*` разрешены только интернет и DNS WSL (10.255.255.254:53); запрещены 10/8, 172.16/12 (хост Windows, docker), 192.168/16 (LAN), 100.64/10 (tailnet), 169.254/16, 127/8, мультикаст, IPv6, сама VM (Ubuntu, docker 18789/18790/8088) и сестра. Процесс ребёнка вне своего netns не имеет сети вовсе (`meta skuid` drop). Docker в Ubuntu ставит FORWARD DROP — в `DOCKER-USER` добавлены два правила ACCEPT только для `afh+`. Чужие правила не трогаются.
- **Лимиты** (`/etc/aiwb-family/sandbox.conf`, в репозитории `files/sandbox.conf`): все дети вместе 6 ядер / 8 ГБ / 1024 процесса, каждый 4 ядра / 4 ГБ RAM + 1 ГБ swap / 512 процессов. Диск — образ ext4 на пользователя (`/var/lib/aiwb-family/homes/<user>.img`, 10 ГБ, nosuid,nodev), `/tmp` ребёнка на нём же. Файлы `/tmp` старше 3 дней удаляются при загрузке и раз в час.
- **Пакеты**: build-essential, gcc/g++ 13, gdb, strace, cmake, make, python3 + venv/pip (pip ставит только в venv, PEP 668), Node.js — новейший LTS с nodejs.org (sha256, `/opt/node`; `npm i -g` идёт в `~/.local`), dotnet-sdk-8.0, openjdk-21, rustc/cargo из Ubuntu (rustup на пользователя — `--rustup`), git, ripgrep.
- wsl-throttle не менялся: он ограничивает cgroup `aiwb-throttle` очереди CoreScoping в Ubuntu, песочница — свою `aiwb-family`; они не пересекаются.

## Вызов

```
python wsl-family-sandbox.py create [--users boris:3001 cora:3002]  # по умолчанию — члены из install.json, uid с 3001 [--disk-gb 10] [--vhd-size 40GB] [--rustup]
python wsl-family-sandbox.py update                 # залить изменённые files/* и пересобрать aiwb-enter
python wsl-family-sandbox.py status [--wake]        # лимиты, расход, firewall-счётчики, сторож, лог
python wsl-family-sandbox.py add-user NAME [--uid N] [--disk-gb 10] [--rustup]
python wsl-family-sandbox.py reset-user NAME [--disk-gb N]   # убить процессы, стереть образ, заново /etc/skel
python wsl-family-sandbox.py del-user NAME
python wsl-family-sandbox.py check [--user boris] [--deny 192.168.1.10 ...]   # hello на 7 языках + отказы + сеть
python wsl-family-sandbox.py run USER 'команда'
python wsl-family-sandbox.py destroy --yes          # снять firewall/cgroup из общей VM и unregister
```

uid детей — с 3001: правило `meta skuid` действует на всю VM, а в Ubuntu uid 1001+ может появиться у обычного пользователя. Лимиты меняются правкой `files/sandbox.conf` → `update` (на существующем дистрибутиве — правкой `/etc/aiwb-family/sandbox.conf` от root и `aiwb-family-boot cgroups`).

## Проверено (12.11.2026)

`check` от обоих пользователей песочницы: C, C++, Python (+venv), Node 26 LTS, .NET 8 (`dotnet new console && dotnet run`), Java 21, Rust — PASS; `ls /mnt` пусто, `cat /etc/shadow`, `ls /home/<сестра>`, `cmd.exe`, `powershell.exe`, PE-файл, `sudo`, `unshare -U` — отказ; не видны `/mnt/wsl`, WSLg, драйверы Windows, процессы и cgroup VM; pypi и npm доступны; 172.24.64.1 (Windows), IP VM и её порты docker 18789/18790/4369, 172.17.0.1, 192.168.1.1, 100.100.100.100 — закрыты (счётчики nft растут; от root дистрибутива 18789 отвечает 200). Лимиты: 6 ГБ в Python → OOM-kill на 4 ГБ; 8 нагруженных потоков → 4,02 ядра; 600 фоновых процессов → упор в pids.max; `fallocate 11G` → ENOSPC. `wsl -u <user> -e sleep 30` — сторож убил за ~2,5 с, сети у такого процесса нет. Ubuntu не задета: CoreScoping идёт (aiwb-throttle), docker-контейнеры работают, интернет есть.

## Ограничения и остаточный риск

- Ядро, cgroup-дерево и сетевое пространство у всех дистрибутивов WSL одни: побег через уязвимость ядра даёт доступ ко всей VM (Ubuntu, её файлы и сеть). Снижено: нет root, no_new_privs, seccomp без user namespaces, блочные устройства `/dev/sd*` только для root:disk.
- Корневая ФС дистрибутива для детей только для чтения (единственный общий каталог с записью `/var/crash` закрыт, `/tmp` и `/var/tmp` — свои), но общая: они видят список установленных пакетов и `/etc` (без `shadow`). Абстрактные unix-сокеты видны только внутри своего netns (у каждой сестры свой).
- Публичный IP домашнего роутера (hairpin) не закрыт явно; IPv6 из песочницы выключен.
- Окно WSL2 VM общее: `.wslconfig` (память, `vmIdleTimeout=-1`) действует и на песочницу; `wsl --shutdown` гасит и её. Пользователь `aiwbsvc` (по умолчанию) — без оболочки; root дистрибутива доступен с Windows через `wsl -u root`, это и есть граница доверия Bridge.
- `wsl -d aiwb-family -u <ребёнок> -e <prog>` минует оболочку входа: процесс без сети и убивается сторожем за ≤ 2–4 с, но эти секунды он вне лимитов cgroup. Bridge должен вызывать только формой выше.

## История

- 0.1.4 — 2026-11-13 — члены домохозяйства по умолчанию — через `install/aiwb_install.py` ядра Freedom: `AIWB_FREEDOM` (старое имя `AIWB_SOURCE`), затем `repo` из `%LOCALAPPDATA%\AIWorkbench\devtree.json`, затем `C:\AIWorkbench\freedom`; архив `C:\AIWorkbench\source` больше не нужен.
- 0.1.3 — 2026-11-13 — тексты без имён членов семьи (экспорт Freedom); поведение не изменилось.
- 0.1.2 — 2026-11-12 — `add-user`/`create` отказываются от существующей учётки дистрибутива вне uid 3001–3998 (root, daemon …): раньше она переводилась в песочницу (ревью шага 6a мастера Freedom).

- 0.1.1 — 2026-11-12 — пользователи по умолчанию — члены домохозяйства из install.json (`AIWB_SANDBOX_USERS` для замены), без имён в коде и документации (Freedom F7); у владельца результат тот же.

- 0.1.0 — 2026-11-12 — первая версия: дистрибутив aiwb-family, setuid-вход aiwb-enter (cgroup, netns, mount/cgroup ns, seccomp, no_new_privs), сторож, nft-изоляция, образы домов, проверка `check`.

// aiwb-enter: setuid-root launcher of the aiwb-family sandbox.
// Puts the calling family user into its cgroup, its own network namespace and a private
// mount namespace (WSL mounts hidden, own /tmp, /dev/shm, cgroup and /proc views), drops root,
// sets no_new_privs and a seccomp filter, then execs the command as that user.
// Installed as /usr/local/libexec/aiwb-enter (root:aiwbfam 4750); /usr/local/bin/aiwb-shell is
// a symlink to it and is the login shell of family users ("aiwb-shell -c cmd" = "bash -c cmd").
#define _GNU_SOURCE
#include <errno.h>
#include <fcntl.h>
#include <grp.h>
#include <linux/audit.h>
#include <linux/filter.h>
#include <linux/seccomp.h>
#include <pwd.h>
#include <sched.h>
#include <stddef.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/mount.h>
#include <sys/prctl.h>
#include <sys/stat.h>
#include <sys/syscall.h>
#include <unistd.h>

#define CGROOT "/sys/fs/cgroup/aiwb-family"
#define NETNS_DIR "/run/netns"
#define USERS "/etc/aiwb-family/users"

static void die(const char *what) {
    fprintf(stderr, "aiwb-enter: %s: %s\n", what, errno ? strerror(errno) : "refused");
    exit(126);
}

// USERS: "name uid disk_gb" per line, root-owned
static int listed(const char *name) {
    FILE *f = fopen(USERS, "re");
    if (!f) return 0;
    struct stat st;
    if (fstat(fileno(f), &st) || st.st_uid != 0 || (st.st_mode & 022)) { fclose(f); return 0; }
    char line[256], n[64];
    int ok = 0;
    while (fgets(line, sizeof line, f))
        if (sscanf(line, "%63s", n) == 1 && n[0] != '#' && strcmp(n, name) == 0) { ok = 1; break; }
    fclose(f);
    return ok;
}

static int hidden(const char *mp) {
    static const char *pre[] = {"/mnt/", "/usr/lib/wsl", "/tmp/.X11-unix", "/run/aiwb", "/run/WSL", NULL};
    for (int i = 0; pre[i]; i++)
        if (strncmp(mp, pre[i], strlen(pre[i])) == 0) return 1;
    return 0;
}

// detach WSL mounts (Windows drives, /mnt/wsl shared by all distros, WSLg sockets, drivers)
static void hide_mounts(void) {
    for (int pass = 0; pass < 8; pass++) {
        FILE *f = fopen("/proc/self/mountinfo", "re");
        if (!f) die("mountinfo");
        char line[4096], mp[2048];
        int left = 0;
        while (fgets(line, sizeof line, f)) {
            if (sscanf(line, "%*s %*s %*s %*s %2047s", mp) != 1) continue;
            if (!hidden(mp)) continue;
            left++;
            umount2(mp, MNT_DETACH);
        }
        fclose(f);
        if (!left) return;
    }
    errno = 0;
    die("cannot hide WSL mounts");
}

static void seccomp_filter(void) {
    struct sock_filter f[] = {
        BPF_STMT(BPF_LD | BPF_W | BPF_ABS, offsetof(struct seccomp_data, arch)),
        BPF_JUMP(BPF_JMP | BPF_JEQ | BPF_K, AUDIT_ARCH_X86_64, 1, 0),
        BPF_STMT(BPF_RET | BPF_K, SECCOMP_RET_KILL_PROCESS),
        BPF_STMT(BPF_LD | BPF_W | BPF_ABS, offsetof(struct seccomp_data, nr)),
        BPF_JUMP(BPF_JMP | BPF_JGE | BPF_K, 0x40000000, 0, 1),  // x32 ABI
        BPF_STMT(BPF_RET | BPF_K, SECCOMP_RET_ERRNO | EPERM),
        BPF_JUMP(BPF_JMP | BPF_JEQ | BPF_K, __NR_clone3, 0, 1),  // glibc falls back to clone
        BPF_STMT(BPF_RET | BPF_K, SECCOMP_RET_ERRNO | ENOSYS),
        BPF_JUMP(BPF_JMP | BPF_JEQ | BPF_K, __NR_io_uring_setup, 0, 1),
        BPF_STMT(BPF_RET | BPF_K, SECCOMP_RET_ERRNO | ENOSYS),
        BPF_JUMP(BPF_JMP | BPF_JEQ | BPF_K, __NR_bpf, 0, 1),
        BPF_STMT(BPF_RET | BPF_K, SECCOMP_RET_ERRNO | EPERM),
        BPF_JUMP(BPF_JMP | BPF_JEQ | BPF_K, __NR_userfaultfd, 0, 1),
        BPF_STMT(BPF_RET | BPF_K, SECCOMP_RET_ERRNO | EPERM),
        BPF_JUMP(BPF_JMP | BPF_JEQ | BPF_K, __NR_perf_event_open, 0, 1),
        BPF_STMT(BPF_RET | BPF_K, SECCOMP_RET_ERRNO | EPERM),
        BPF_JUMP(BPF_JMP | BPF_JEQ | BPF_K, __NR_unshare, 1, 0),
        BPF_JUMP(BPF_JMP | BPF_JEQ | BPF_K, __NR_clone, 0, 3),
        BPF_STMT(BPF_LD | BPF_W | BPF_ABS, offsetof(struct seccomp_data, args[0])),
        BPF_JUMP(BPF_JMP | BPF_JSET | BPF_K, CLONE_NEWUSER, 0, 1),  // no user namespaces
        BPF_STMT(BPF_RET | BPF_K, SECCOMP_RET_ERRNO | EPERM),
        BPF_STMT(BPF_RET | BPF_K, SECCOMP_RET_ALLOW),
    };
    struct sock_fprog prog = {.len = sizeof f / sizeof f[0], .filter = f};
    if (prctl(PR_SET_SECCOMP, SECCOMP_MODE_FILTER, &prog)) die("seccomp");
}

int main(int argc, char **argv) {
    if (argc < 1 || !argv[0]) return 2;
    uid_t uid = getuid();
    struct passwd *pw = getpwuid(uid);
    if (!pw || uid < 1000 || !listed(pw->pw_name)) { errno = EPERM; die("not a sandbox user"); }
    char name[64], home[512], cwd[4096], path[1024];
    snprintf(name, sizeof name, "%s", pw->pw_name);
    snprintf(home, sizeof home, "%s", pw->pw_dir);
    gid_t gid = pw->pw_gid;
    if (!getcwd(cwd, sizeof cwd)) snprintf(cwd, sizeof cwd, "%s", home);

    // login-shell mode: argv[0] is "aiwb-shell" or starts with '-'
    const char *base = strrchr(argv[0], '/');
    base = base ? base + 1 : argv[0];
    int shell = argv[0][0] == '-' || strcmp(base, "aiwb-shell") == 0;
    if (!shell && argc < 2) {
        fprintf(stderr, "usage: aiwb-enter <command> [args...]\n");
        return 2;
    }

    // 0. right after a distro start the boot script may still be preparing cgroups and netns
    struct stat st;
    for (int i = 0; i < 30 && stat("/run/aiwb/ready", &st); i++) usleep(500000);

    // 1. cgroup (limits); "0" moves the writer
    snprintf(path, sizeof path, CGROOT "/%s/cgroup.procs", name);
    int fd = open(path, O_WRONLY | O_CLOEXEC);
    if (fd < 0 || dprintf(fd, "0\n") < 0) die("sandbox cgroup (is the distro booted?)");
    close(fd);

    // 2. own network namespace (internet only, no LAN/tailnet/other distros)
    snprintf(path, sizeof path, NETNS_DIR "/aiwb-%s", name);
    fd = open(path, O_RDONLY | O_CLOEXEC);
    if (fd < 0 || setns(fd, CLONE_NEWNET)) die("sandbox netns");
    close(fd);

    // 3. private mount + cgroup namespaces
    if (unshare(CLONE_NEWNS | CLONE_NEWCGROUP)) die("unshare");
    if (mount(NULL, "/", NULL, MS_REC | MS_PRIVATE, NULL)) die("make-rprivate");
    hide_mounts();
    // the empty mount points /mnt/c, /mnt/wsl ... stay in the rootfs: cover /mnt with an empty read-only tmpfs
    if (mount("tmpfs", "/mnt", "tmpfs", MS_RDONLY | MS_NOSUID | MS_NODEV | MS_NOEXEC, "size=4k,mode=755"))
        die("cover /mnt");
    // interop sockets of WSL init (interop is off in wsl.conf; hide them anyway)
    if (access("/run/WSL", F_OK) == 0 &&
        mount("tmpfs", "/run/WSL", "tmpfs", MS_RDONLY | MS_NOSUID | MS_NODEV | MS_NOEXEC, "size=4k,mode=755"))
        die("cover /run/WSL");
    if (umount2("/sys/fs/cgroup", MNT_DETACH) ||
        mount("cgroup2", "/sys/fs/cgroup", "cgroup2", MS_NOSUID | MS_NODEV | MS_NOEXEC | MS_RDONLY, NULL))
        die("cgroup view");
    if (mount("proc", "/proc", "proc", MS_NOSUID | MS_NODEV | MS_NOEXEC, "hidepid=invisible")) die("proc");
    if (mount("tmpfs", "/dev/shm", "tmpfs", MS_NOSUID | MS_NODEV, "size=256m,mode=1777")) die("shm");
    // /tmp -> $HOME/.tmp (on the user's disk image, counts against the quota)
    int hfd = open(home, O_PATH | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC);
    if (hfd < 0) die("home");
    int tfd = openat(hfd, ".tmp", O_PATH | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC);
    if (tfd < 0 && errno == ENOENT) {
        if (mkdirat(hfd, ".tmp", 0700) == 0) {
            if (fchownat(hfd, ".tmp", uid, gid, AT_SYMLINK_NOFOLLOW)) die("chown tmp");
            tfd = openat(hfd, ".tmp", O_PATH | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC);
        }
    }
    if (tfd < 0 || fstat(tfd, &st) || st.st_uid != uid || !S_ISDIR(st.st_mode)) die("$HOME/.tmp");
    snprintf(path, sizeof path, "/proc/self/fd/%d", tfd);
    if (mount(path, "/tmp", NULL, MS_BIND, NULL) ||
        mount(NULL, "/tmp", NULL, MS_REMOUNT | MS_BIND | MS_NOSUID | MS_NODEV, NULL))
        die("bind /tmp");
    if (mount(path, "/var/tmp", NULL, MS_BIND, NULL) ||
        mount(NULL, "/var/tmp", NULL, MS_REMOUNT | MS_BIND | MS_NOSUID | MS_NODEV, NULL))
        die("bind /var/tmp");
    close(tfd);
    close(hfd);

    // 4. drop root for good
    if (initgroups(name, gid) || setresgid(gid, gid, gid) || setresuid(uid, uid, uid)) die("drop privileges");
    if (setuid(0) == 0 || seteuid(0) == 0) { errno = EPERM; die("still root"); }
    if (prctl(PR_SET_NO_NEW_PRIVS, 1, 0, 0, 0)) die("no_new_privs");
    seccomp_filter();
    if (chdir(cwd) && chdir(home)) die("chdir");
    setenv("TMPDIR", "/tmp", 1);
    setenv("AIWB_SANDBOX", "1", 1);
    // WSLg variables point into the hidden /mnt/wslg; a missing XDG_RUNTIME_DIR breaks e.g. .NET named mutexes
    unsetenv("DISPLAY");
    unsetenv("WAYLAND_DISPLAY");
    unsetenv("PULSE_SERVER");
    unsetenv("WSL2_GUI_APPS_ENABLED");
    if (mkdir("/tmp/.run", 0700) && errno != EEXIST) die("mkdir /tmp/.run");
    if (lstat("/tmp/.run", &st) || !S_ISDIR(st.st_mode) || st.st_uid != uid) die("/tmp/.run");
    setenv("XDG_RUNTIME_DIR", "/tmp/.run", 1);

    if (shell) {
        // keep the login dash and all arguments, run bash instead of ourselves
        argv[0] = argv[0][0] == '-' ? "-bash" : "bash";
        execv("/bin/bash", argv);
        die("exec bash");
    }
    execvp(argv[1], argv + 1);
    die(argv[1]);
}

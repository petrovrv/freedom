"""Static security scan of library tools (toolbox 1.5.0): secrets in code, hosts outside the allowlist, exec of
downloaded content, TLS checks turned off, writes outside the allowed folders, destructive deletes, shell injection,
unsafe deserialization. Stdlib only. Python files are read through ast; .ps1/.js/.mjs/.sh/.cmd/.bat by regex.

Patterns below are written with character classes (e.g. "verif[y]") so that this file does not match its own rules.
"""
import ast
import fnmatch
import json
import math
import os
import re
from dataclasses import dataclass, asdict
from pathlib import Path

ERROR, WARN = "error", "warn"

RULES = {
    "secret-literal": (ERROR, "secret (key, token, password) written in the code"),
    "exec-remote": (ERROR, "downloaded content is executed (eval/exec/iex/| sh)"),
    "tls-off": (ERROR, "TLS certificate or host name check turned off"),
    "destructive": (ERROR, "recursive delete of a root-like literal path"),
    "host-not-allowed": (WARN, "network host outside the allowlist (ALLOWED_HOSTS or allowed_hosts in SKILL.md)"),
    "write-outside": (WARN, "write to a literal path outside the allowed folders"),
    "shell-injection": (WARN, "shell command built from non-literal parts"),
    "dynamic-exec": (WARN, "eval/exec/new Function of non-literal code"),
    "unsafe-deserialize": (WARN, "pickle/marshal/yaml.load of data that may be untrusted"),
    "exception": (ERROR, "invalid security exception in SKILL.md"),
    "stale-exception": (WARN, "security exception in SKILL.md matches nothing"),
}

# Hosts every tool may contact (suffix match: "googleapis.com" covers "www.googleapis.com").
# XML/namespace hosts are listed because they appear as URIs, not as network calls.
ALLOWED_HOSTS = (
    "localhost", "127.0.0.1", "::1",
    "googleapis.com", "google.com", "gstatic.com", "googleusercontent.com",
    "github.com", "githubusercontent.com", "api.github.com",
    "anthropic.com", "claude.ai", "openai.com", "chatgpt.com",
    "linkedin.com", "licdn.com", "whatsapp.com", "whatsapp.net",
    "python.org", "pypi.org", "pythonhosted.org", "npmjs.org", "npmjs.com",
    "schemas.openxmlformats.org", "schemas.microsoft.com", "w3.org", "purl.org",
)


def _install_hosts() -> tuple:
    """Own hosts of this install (toolbox 1.6.2, Freedom): AIWB_ALLOWED_HOSTS (comma list), the site of install.json
    (AIWB_SITE_URL, else site.url) and its parent domain, so co.example.org also allows tools.example.org."""
    from urllib.parse import urlsplit
    hosts = [h.strip().lower() for h in os.environ.get("AIWB_ALLOWED_HOSTS", "").split(",") if h.strip()]
    url = os.environ.get("AIWB_SITE_URL")
    if not url:
        if os.environ.get("AIWB_INSTALL"):
            cfg = Path(os.environ["AIWB_INSTALL"])
        elif os.name == "nt" and os.environ.get("LOCALAPPDATA"):
            cfg = Path(os.environ["LOCALAPPDATA"]) / "AIWorkbench" / "install.json"
        else:
            cfg = Path.home() / ".config" / "aiwb" / "install.json"
        try:
            url = (json.loads(cfg.read_text(encoding="utf-8-sig")).get("site") or {}).get("url")
        except (OSError, ValueError, AttributeError):
            url = None
    host = (urlsplit(url).hostname or "") if url else ""
    if host and "." in host:
        hosts.append(host)
        labels = host.split(".")
        if len(labels) >= 3 and not host.replace(".", "").isdigit():
            hosts.append(".".join(labels[1:]))
    return tuple(dict.fromkeys(hosts))


ALLOWED_HOSTS = ALLOWED_HOSTS + _install_hosts()

CODE_EXT = {".py", ".ps1", ".psm1", ".js", ".mjs", ".cjs", ".ts", ".sh", ".cmd", ".bat"}

# Folders a tool may write to by a literal path. Paths from arguments, %TEMP%, %LOCALAPPDATA% and other
# environment-derived paths are not literal and are not checked. Temp and AIWorkbench* of the current user's
# %LOCALAPPDATA% are allowed (1.6.2: from the environment, no user name in the code).
_LOCAL = (os.environ.get("LOCALAPPDATA") or str(Path.home() / "AppData" / "Local")).replace("/", "\\").rstrip("\\").lower()
ALLOWED_WRITE_ROOTS = (
    r"c:\aiworkbench",
    r"g:\my drive\ai-workbench\projects",
    r"g:\my drive\ai-workbench\transport",
    r"g:\my drive\ai-workbench\backups",
    r"g:\my drive\ai-workbench\lab",
    _LOCAL + r"\temp",
    _LOCAL + r"\aiworkbench",
    "/tmp",
)
ROOT_LIKE = re.compile(r"^(?:[a-z]:\\?|/|~|\\\\|[a-z]:\\users(?:\\[^\\]+)?|/home(?:/[^/]+)?|g:\\my drive(?:\\ai-workbench)?)\\?$")

PLACEHOLDER = re.compile(r"(?i)xxx|<[^>]*>|\$\{|\{\}|example|your[_-]|changeme|dummy|placeholder|redacted|\*\*\*|^test")
SECRET_NAME = re.compile(r"(?i)(passw(or)?d|passwd|secret|api[_-]?key|apikey|access[_-]?key|auth[_-]?token|"
                         r"(^|_)token$|bearer|private[_-]?key|client[_-]?secret)")
SECRET_PATTERNS = [
    re.compile(r"-----BEGIN (?:RSA |EC |DSA |OPENSSH |ENCRYPTED )?PRIVAT[E] KEY-----"),
    re.compile(r"\bAKI[A]\w{16}\b"),                                    # AWS access key id
    re.compile(r"\bgh[pousr]_[A-Za-z0-9]{36,}\b"),                     # GitHub tokens
    re.compile(r"\bgithub_pa[t]_[A-Za-z0-9_]{60,}\b"),
    re.compile(r"\bAIz[a]Sy[A-Za-z0-9_\-]{33}\b"),                      # Google API key
    re.compile(r"\bya2[9]\.[A-Za-z0-9_\-]{30,}"),                       # Google OAuth access token
    re.compile(r"\b1//0[A-Za-z0-9_\-]{40,}"),                           # Google refresh token
    re.compile(r"\bGOCSP[X]-[A-Za-z0-9_\-]{20,}"),                      # Google OAuth client secret
    re.compile(r"\bsk-an[t]-[A-Za-z0-9_\-]{30,}"),                      # Anthropic
    re.compile(r"\bsk-(?:pro[j]-)?[A-Za-z0-9_\-]{40,}"),                # OpenAI
    re.compile(r"\bxox[abprs]-[A-Za-z0-9\-]{20,}"),                     # Slack
    re.compile(r"\beyJ[A-Za-z0-9_\-]{20,}\.eyJ[A-Za-z0-9_\-]{20,}\.[A-Za-z0-9_\-]{20,}"),  # JWT
]

TLS_PATTERNS = [
    re.compile(r"\bverif[y]\s*=\s*False\b"),
    re.compile(r"_create_unverified_contex[t]"),
    re.compile(r"\bCERT_NON[E]\b"),
    re.compile(r"check_hostnam[e]\s*=\s*False"),
    re.compile(r"-SkipCertificateChec[k]"),
    re.compile(r"ServerCertificateValidationCallbac[k]\s*=\s*\{\s*\$?true"),
    re.compile(r"NODE_TLS_REJECT_UNAUTHORIZE[D]\s*[=:]\s*['\"]?0"),
    re.compile(r"rejectUnauthorize[d]\s*:\s*false"),
    re.compile(r"ignoreHTTPSError[s]\s*:\s*true|ignore_https_error[s]\s*=\s*True"),
    re.compile(r"--ignore-certificate-error[s]"),
    re.compile(r"\b(?:curl|wget)\b[^\n|;]*\s(?:--insecur[e]|-k\b|--no-check-certificat[e])"),
]

EXEC_REMOTE_PATTERNS = [
    re.compile(r"\b(?:curl|wget)\b[^\n;]*\|\s*(?:sudo\s+)?(?:ba|z)?s[h]\b"),
    re.compile(r"(?i)\b(?:Invoke-WebRequest|Invoke-RestMethod|iwr|irm)\b[^\n;]*\|\s*(?:Invoke-Expressio[n]|ie[x])\b"),
    re.compile(r"(?i)\b(?:Invoke-Expressio[n]|ie[x])\b[^\n]*(?:DownloadString|Invoke-WebRequest|Invoke-RestMethod|\biwr\b|\birm\b)"),
    re.compile(r"\beva[l]\s*\(\s*await\s+(?:fetch|\w+\.text\(\))"),
]
JS_DYNAMIC_EXEC = re.compile(r"(?<![.\w])eva[l]\s*\((?!\s*['\"`])|\bnew\s+Functio[n]\s*\(")
PS_DYNAMIC_EXEC = re.compile(r"(?i)\b(?:Invoke-Expressio[n]|ie[x])\b")
SH_RM_RF = re.compile(r"\brm\s+-(?:rf|fr|r)\b\s+['\"]?(\S+?)['\"]?(?:\s|$)")
PS_RM_RECURSE = re.compile(r"(?i)\bRemove-Ite[m]\b[^\n]*-Recurse[^\n]*?(?:-(?:Literal)?Path\s+)?['\"]([^'\"]+)['\"]")
JS_WRITE = re.compile(r"\b(?:writeFileSync|appendFileSync|writeFile|appendFile|createWriteStream)\s*\(\s*['\"`]([^'\"`]+)['\"`]")
PS_WRITE = re.compile(r"(?i)\b(?:Out-File|Set-Content|Add-Content)\b[^\n]*?(?:-(?:Literal)?Path\s+|-FilePath\s+|\s)['\"]([A-Za-z]:\\[^'\"]+)['\"]")
URL_RE = re.compile(r"\b(?:https?|wss?)://([A-Za-z0-9.\-]+|\[[0-9a-fA-F:]+\])")
COMMENT_LINE = re.compile(r"^\s*(?:#|//|::|rem\s|REM\s|\*|/\*)")


@dataclass
class Finding:
    tool: str
    file: str
    line: int
    rule: str
    level: str
    detail: str
    snippet: str
    excused: str = ""   # reason from SKILL.md when an exception covers the finding

    def as_dict(self):
        return asdict(self)


# ------------------------------------------------------------------ SKILL.md: exceptions and allowed hosts

def parse_security_meta(skill: Path) -> tuple[list[dict], list[str], list[str]]:
    """Return (exceptions, allowed_hosts, problems) from the SKILL.md frontmatter.

    security:
      - rule: host-not-allowed
        file: job-boards.py        # optional glob, relative to the tool folder
        match: jobbnorge           # optional substring of the finding's line or detail
        reason: why it is safe
    allowed_hosts: [a.example.org, b.example.org]
    """
    text = skill.read_text(encoding="utf-8").lstrip("\ufeff")
    m = re.match(r"^---\r?\n(.*?)\r?\n---", text, re.S)
    if not m:
        return [], [], []
    lines = m.group(1).splitlines()
    excs, hosts, probs = [], [], []
    i = 0
    while i < len(lines):
        line = lines[i]
        key = line.split(":", 1)[0].strip() if ":" in line and not line.startswith((" ", "-")) else None
        if key == "allowed_hosts":
            val = line.split(":", 1)[1].strip()
            if val.startswith("[") and val.endswith("]"):
                hosts += [h.strip().strip("'\"").lower() for h in val[1:-1].split(",") if h.strip()]
            else:
                probs.append("allowed_hosts must be a one-line list: [host1, host2]")
        if key == "security":
            cur = None
            i += 1
            while i < len(lines) and (lines[i].startswith((" ", "-")) or not lines[i].strip()):
                raw = lines[i]
                s = raw.strip()
                i += 1
                if not s or s.startswith("#"):
                    continue
                if s.startswith("- "):
                    cur = {}
                    excs.append(cur)
                    s = s[2:].strip()
                if cur is None or ":" not in s:
                    probs.append(f"security: cannot parse line {raw!r}")
                    continue
                k, v = s.split(":", 1)
                v = v.strip()
                if len(v) >= 2 and v[0] == v[-1] and v[0] in "'\"":
                    v = v[1:-1]
                cur[k.strip()] = v
            continue
        i += 1
    for e in excs:
        if e.get("rule") not in RULES or e.get("rule") in ("exception", "stale-exception"):
            probs.append(f"security: unknown rule {e.get('rule')!r}")
        if len(e.get("reason", "").strip()) < 10:
            probs.append(f"security: exception for {e.get('rule')!r} needs a reason (at least 10 characters)")
    return excs, hosts, probs


# ------------------------------------------------------------------ helpers

def host_allowed(host: str, extra: list[str]) -> bool:
    host = host.lower().strip("[]").rstrip(".")
    if not host or "." not in host and host not in ("localhost",) and ":" not in host:
        return True  # templated or relative (e.g. https://{host}/...): nothing to judge
    for a in (*ALLOWED_HOSTS, *extra):
        a = a.lower()
        if host == a or host.endswith("." + a):
            return True
    return False


def norm_path(p: str) -> str:
    return p.replace("/", "\\").rstrip("\\").lower() if re.match(r"^[A-Za-z]:", p) else p.rstrip("/").lower()


def is_abs_literal(p: str) -> bool:
    return bool(re.match(r"^(?:[A-Za-z]:[\\/]|/(?!/)|~[\\/]|\\\\)", p))


def write_allowed(p: str) -> bool:
    n = norm_path(p)
    if n.startswith("~"):
        return False
    return any(n == r or n.startswith(r + ("\\" if ":" in r else "/")) for r in ALLOWED_WRITE_ROOTS)


def root_like(p: str) -> bool:
    return bool(ROOT_LIKE.match(norm_path(p) or "/"))


def entropy(s: str) -> float:
    if not s:
        return 0.0
    counts = {c: s.count(c) for c in set(s)}
    return -sum(n / len(s) * math.log2(n / len(s)) for n in counts.values())


def looks_secret(value: str) -> bool:
    if len(value) < 12 or any(c.isspace() for c in value) or PLACEHOLDER.search(value):
        return False
    if is_abs_literal(value) or value.startswith(("http://", "https://")) or re.fullmatch(r"[a-z_./\-]+", value):
        return False  # paths, URLs, identifiers such as keyring service names
    if re.fullmatch(r"[A-Za-z][\w.\-]*/[\w.\-/]+", value):
        return False  # aiwb/service style names
    return entropy(value) >= 3.3 and bool(re.search(r"\d", value)) and bool(re.search(r"[A-Za-z]", value))


# ------------------------------------------------------------------ Python (ast)

class PyScanner(ast.NodeVisitor):
    def __init__(self, src: str, report):
        self.src_lines = src.splitlines()
        self.report = report
        self.consts: dict[str, ast.AST] = {}   # module-level NAME = <expr>
        self.tainted: set[str] = set()         # names assigned from network calls
        self.docstrings: set[int] = set()

    # -- utilities
    def line(self, node) -> str:
        n = getattr(node, "lineno", 0)
        return self.src_lines[n - 1].strip() if 0 < n <= len(self.src_lines) else ""

    @staticmethod
    def dotted(node) -> str:
        parts = []
        while isinstance(node, ast.Attribute):
            parts.append(node.attr)
            node = node.value
        if isinstance(node, ast.Name):
            parts.append(node.id)
        elif isinstance(node, ast.Call):
            parts.append(PyScanner.dotted(node.func) + "()")
        return ".".join(reversed(parts))

    def literal_root(self, node, depth=0):
        """Best-effort literal path an expression starts from; '__file__' for paths next to the script."""
        if depth > 8 or node is None:
            return None
        if isinstance(node, ast.Constant) and isinstance(node.value, str):
            return node.value
        if isinstance(node, ast.Name):
            if node.id == "__file__":
                return "__file__"
            if node.id in self.consts:
                return self.literal_root(self.consts[node.id], depth + 1)
            return None
        if isinstance(node, ast.JoinedStr):
            first = node.values[0] if node.values else None
            if isinstance(first, ast.Constant):
                return first.value
            if isinstance(first, ast.FormattedValue):
                return self.literal_root(first.value, depth + 1)
            return None
        if isinstance(node, ast.BinOp) and isinstance(node.op, (ast.Div, ast.Add)):
            return self.literal_root(node.left, depth + 1)
        if isinstance(node, ast.BoolOp):  # os.environ.get(X) or r"C:\..." -> the literal fallback
            for v in node.values:
                r = self.literal_root(v, depth + 1)
                if r:
                    return r
            return None
        if isinstance(node, ast.Attribute) and node.attr in ("parent", "parents"):
            return self.literal_root(node.value, depth + 1)
        if isinstance(node, ast.Subscript):
            return self.literal_root(node.value, depth + 1)
        if isinstance(node, ast.Call):
            name = self.dotted(node.func)
            if name.endswith(("Path", "PurePath", "WindowsPath", "path.join", "joinpath", "resolve", "absolute",
                              "with_name", "with_suffix", "expanduser", "expandvars", "abspath", "normpath",
                              "realpath", "dirname")):
                if isinstance(node.func, ast.Attribute) and not name.endswith(("path.join", "Path", "PurePath",
                                                                               "WindowsPath", "abspath", "normpath",
                                                                               "realpath", "dirname", "expanduser",
                                                                               "expandvars")):
                    return self.literal_root(node.func.value, depth + 1)
                if node.args:
                    return self.literal_root(node.args[0], depth + 1)
        return None

    def check_write_target(self, node, target):
        root = self.literal_root(target)
        if root is None:
            return
        if root == "__file__":
            self.report("write-outside", node, "writes into the tool folder (next to the script)")
        elif is_abs_literal(root) and not write_allowed(root):
            self.report("write-outside", node, f"writes under {root}")

    def is_net_expr(self, node) -> bool:
        for sub in ast.walk(node):
            if isinstance(sub, ast.Name) and sub.id in self.tainted:
                return True
            if isinstance(sub, ast.Call):
                name = self.dotted(sub.func)
                if re.search(r"(?:^|\.)(?:urlopen|urlretrieve)$", name) or name.startswith(("requests.", "httpx.", "urllib.request")):
                    return True
        return False

    # -- visitors
    def visit_Module(self, node):
        for st in node.body:
            if isinstance(st, ast.Assign) and len(st.targets) == 1 and isinstance(st.targets[0], ast.Name):
                self.consts[st.targets[0].id] = st.value
        for n in ast.walk(node):
            if isinstance(n, (ast.Module, ast.FunctionDef, ast.AsyncFunctionDef, ast.ClassDef)) and n.body:
                b = n.body[0]
                if isinstance(b, ast.Expr) and isinstance(b.value, ast.Constant) and isinstance(b.value.value, str):
                    self.docstrings.add(id(b.value))
        self.generic_visit(node)

    def visit_Assign(self, node):
        if self.is_net_expr(node.value):
            for t in node.targets:
                for n in ast.walk(t):
                    if isinstance(n, ast.Name):
                        self.tainted.add(n.id)
        for t in node.targets:
            name = t.id if isinstance(t, ast.Name) else t.attr if isinstance(t, ast.Attribute) else None
            if isinstance(t, ast.Subscript) and isinstance(t.slice, ast.Constant) and isinstance(t.slice.value, str):
                name = t.slice.value
            self.check_named_secret(name, node.value, node)
        self.generic_visit(node)

    def visit_Dict(self, node):
        for k, v in zip(node.keys, node.values):
            if isinstance(k, ast.Constant) and isinstance(k.value, str):
                self.check_named_secret(k.value, v, node)
        self.generic_visit(node)

    def check_named_secret(self, name, value, node):
        if (name and SECRET_NAME.search(name) and isinstance(value, ast.Constant) and isinstance(value.value, str)
                and looks_secret(value.value)):
            self.report("secret-literal", node, f"{name} = '{value.value[:4]}…' ({len(value.value)} chars)")

    def visit_Constant(self, node):
        if isinstance(node.value, str) and id(node) not in self.docstrings:
            for p in SECRET_PATTERNS:
                if p.search(node.value):
                    self.report("secret-literal", node, f"string matches {p.pattern[:30]}")
                    break
            for m in URL_RE.finditer(node.value):
                self.report_host(m.group(1), node)
        self.generic_visit(node)

    def visit_Call(self, node):
        name = self.dotted(node.func)
        short = name.rsplit(".", 1)[-1]
        kw = {k.arg: k.value for k in node.keywords if k.arg}
        # secret passed as keyword argument
        for k, v in kw.items():
            self.check_named_secret(k, v, node)
        # eval / exec / compile
        if name in ("eval", "exec", "compile", "builtins.eval", "builtins.exec"):
            arg = node.args[0] if node.args else None
            if arg is not None and self.is_net_expr(arg):
                self.report("exec-remote", node, f"{name}() of downloaded content")
            elif arg is not None and not isinstance(arg, ast.Constant):
                self.report("dynamic-exec", node, f"{name}() of non-literal code")
        # subprocess: shell=True with built strings; running downloaded code with -c
        if name.startswith("subprocess.") or short in ("Popen", "run", "call", "check_call", "check_output") and name.startswith("sp."):
            sh = kw.get("shell")
            arg = node.args[0] if node.args else kw.get("args")
            if isinstance(sh, ast.Constant) and sh.value is True and arg is not None and not isinstance(arg, ast.Constant):
                self.report("shell-injection", node, f"{name}(shell=True) with a built command")
            if arg is not None and self.is_net_expr(arg):
                self.report("exec-remote", node, f"{name}() runs downloaded content")
        if isinstance(arg := (node.args[0] if node.args else None), (ast.List, ast.Tuple)) and arg.elts:
            words = [e.value for e in arg.elts if isinstance(e, ast.Constant) and isinstance(e.value, str)]
            if words and Path(words[0]).stem.lower() in ("curl", "wget") and {"-k", "--insecure", "--no-check-certificate"} & set(words):
                self.report("tls-off", node, f"{words[0]} without certificate check")
        if name in ("os.system", "os.popen") and node.args and not isinstance(node.args[0], ast.Constant):
            self.report("shell-injection", node, f"{name}() with a built command")
        # TLS
        if short == "_create_unverified_" + "context":
            self.report("tls-off", node, name)
        v = kw.get("verify")
        if isinstance(v, ast.Constant) and v.value is False:
            self.report("tls-off", node, f"{name}(verify=False)")
        # deserialization
        if name in ("pickle.load", "pickle.loads", "marshal.load", "marshal.loads", "cPickle.load", "cPickle.loads",
                    "dill.load", "dill.loads"):
            self.report("unsafe-deserialize", node, name)
        if name in ("yaml.load", "yaml.load_all", "yaml.unsafe_load"):
            loader = kw.get("Loader") or (node.args[1] if len(node.args) > 1 else None)
            if name == "yaml.unsafe_load" or loader is None or "Safe" not in self.dotted(loader):
                self.report("unsafe-deserialize", node, f"{name} without SafeLoader")
        # writes
        mode = None
        if short == "open" and name in ("open", "io.open", "codecs.open"):
            m = node.args[1] if len(node.args) > 1 else kw.get("mode")
            mode = m.value if isinstance(m, ast.Constant) and isinstance(m.value, str) else None
            if mode and any(c in mode for c in "wax+") and node.args:
                self.check_write_target(node, node.args[0])
        elif short in ("write_text", "write_bytes", "touch", "mkdir") and isinstance(node.func, ast.Attribute):
            if short != "mkdir":
                self.check_write_target(node, node.func.value)
        elif short == "open" and isinstance(node.func, ast.Attribute) and name not in ("io.open", "codecs.open", "os.open"):
            m = node.args[0] if node.args else kw.get("mode")
            if isinstance(m, ast.Constant) and isinstance(m.value, str) and any(c in m.value for c in "wax+"):
                self.check_write_target(node, node.func.value)
        elif name in ("shutil.copy", "shutil.copy2", "shutil.copyfile", "shutil.copytree", "shutil.move",
                      "os.replace", "os.rename") and len(node.args) > 1:
            self.check_write_target(node, node.args[1])
        # destructive deletes
        if name in ("shutil.rmtree", "os.removedirs") and node.args:
            root = self.literal_root(node.args[0])
            if root and root != "__file__" and root_like(root):
                self.report("destructive", node, f"{name}({root})")
        self.generic_visit(node)

    def report_host(self, host, node):
        self.report("host-not-allowed", node, host.lower(), host=host)


def scan_python(path: Path, rel: str, add):
    src = path.read_text(encoding="utf-8", errors="replace")
    try:
        tree = ast.parse(src)
    except SyntaxError as e:
        add(rel, e.lineno or 0, "dynamic-exec", f"cannot parse: {e.msg}", "")
        return
    sc = PyScanner(src, None)

    def report(rule, node, detail, host=None):
        add(rel, getattr(node, "lineno", 0), rule, detail, sc.line(node), host=host)
    sc.report = report
    sc.visit(tree)
    # line-oriented checks the ast does not express well (strings built for other languages, CERT_NONE, ...)
    regex_lines(src, rel, add, py=True)


def regex_lines(src: str, rel: str, add, py=False):
    for no, line in enumerate(src.splitlines(), 1):
        if COMMENT_LINE.match(line):
            continue
        for p in TLS_PATTERNS:
            if p.search(line) and not (py and "verif" in p.pattern and "verify" in line):  # py verify=False via ast
                add(rel, no, "tls-off", re.sub(r"\[bs]|[\[\]\?*+()|{}^$]", "", p.pattern)[:40], line.strip())
                break
        for p in EXEC_REMOTE_PATTERNS:
            if p.search(line):
                add(rel, no, "exec-remote", "downloaded content executed", line.strip())
                break
        if py:
            continue
        for p in SECRET_PATTERNS:
            if p.search(line):
                add(rel, no, "secret-literal", f"string matches {p.pattern[:30]}", line.strip())
                break
        m = re.search(r"""(?i)\b([\w$-]*(?:password|passwd|secret|api_?key|apikey|token))['"]?\s*[:=]\s*['"]([^'"]+)['"]""", line)
        if m and looks_secret(m.group(2)):
            add(rel, no, "secret-literal", f"{m.group(1)} = '{m.group(2)[:4]}…'", line.strip())
        for m in URL_RE.finditer(line):
            add(rel, no, "host-not-allowed", m.group(1).lower(), line.strip(), host=m.group(1))
        ext = Path(rel).suffix.lower()
        if ext in (".js", ".mjs", ".cjs", ".ts") and JS_DYNAMIC_EXEC.search(line):
            add(rel, no, "dynamic-exec", "eval/new Function", line.strip())
        if ext in (".ps1", ".psm1") and PS_DYNAMIC_EXEC.search(line):
            add(rel, no, "dynamic-exec", "Invoke-Expression", line.strip())
        for m in list(SH_RM_RF.finditer(line)) + list(PS_RM_RECURSE.finditer(line)):
            if root_like(m.group(1).strip("'\"")):
                add(rel, no, "destructive", f"recursive delete of {m.group(1)}", line.strip())
        for p in (JS_WRITE, PS_WRITE):
            for m in p.finditer(line):
                if is_abs_literal(m.group(1)) and not write_allowed(m.group(1)):
                    add(rel, no, "write-outside", f"writes under {m.group(1)}", line.strip())


# ------------------------------------------------------------------ driver

def scan_tool(d: Path) -> list[Finding]:
    """All findings of one tool folder, with SKILL.md exceptions applied (excused findings keep their reason)."""
    excs, hosts, probs = parse_security_meta(d / "SKILL.md")
    raw: list[Finding] = []

    def add(rel, line, rule, detail, snippet, host=None):
        if rule == "host-not-allowed" and host_allowed(host or detail, hosts):
            return
        level = RULES[rule][0]
        raw.append(Finding(d.name, rel, line, rule, level, detail, snippet[:200]))

    for f in sorted(d.rglob("*")):
        if not f.is_file() or f.suffix.lower() not in CODE_EXT:
            continue
        rel = f.relative_to(d).as_posix()
        if any(part in ("__pycache__", "node_modules", ".git") or part.startswith(".") for part in f.relative_to(d).parts):
            continue
        if f.suffix.lower() == ".py":
            scan_python(f, rel, add)
        else:
            regex_lines(f.read_text(encoding="utf-8", errors="replace"), rel, add)

    # one finding per (file, line, rule)
    seen, findings = set(), []
    for x in raw:
        k = (x.file, x.line, x.rule, x.detail if x.rule == "host-not-allowed" else "")
        if k not in seen:
            seen.add(k)
            findings.append(x)

    used = [False] * len(excs)
    for x in findings:
        for i, e in enumerate(excs):
            if e.get("rule") != x.rule or len(e.get("reason", "").strip()) < 10:
                continue
            if e.get("file") and not fnmatch.fnmatch(x.file, e["file"]):
                continue
            if e.get("match") and e["match"] not in x.snippet and e["match"] not in x.detail:
                continue
            x.excused = e["reason"]
            used[i] = True
            break
    for p in probs:
        findings.append(Finding(d.name, "SKILL.md", 0, "exception", ERROR, p, ""))
    for i, e in enumerate(excs):
        if not used[i] and e.get("rule") in RULES and len(e.get("reason", "").strip()) >= 10:
            findings.append(Finding(d.name, "SKILL.md", 0, "stale-exception", WARN,
                                    f"rule={e.get('rule')} file={e.get('file', '*')} match={e.get('match', '')}", ""))
    return findings


def summarize(findings: list[Finding]) -> dict:
    active = [f for f in findings if not f.excused]
    return {
        "errors": sum(f.level == ERROR for f in active),
        "warnings": sum(f.level == WARN for f in active),
        "excused": sum(bool(f.excused) for f in findings),
    }


def format_finding(f: Finding) -> str:
    tag = "excused" if f.excused else f.level
    loc = f"{f.tool}/{f.file}" + (f":{f.line}" if f.line else "")
    s = f"{tag:7} {f.rule:18} {loc}  {f.detail}"
    if f.excused:
        s += f"  [{f.excused}]"
    return s

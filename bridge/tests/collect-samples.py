#!/usr/bin/env python3
# SPDX-License-Identifier: AGPL-3.0-or-later
# Collect real agent CLI output streams (stdout.log of Bridge jobs, job_events of the database) as test samples
# for AgentRuntimeTests. Samples contain chat content: keep them outside the repository.
import argparse, json, os, shutil
from collections import Counter

ALLOWED = {"claude-stream-json", "codex-jsonl"}

def load_json(path):
    with open(path, encoding="utf-8-sig") as f:
        return json.load(f)

def write_expected(out, name, proto, result):
    exp = {"protocol": proto}
    if isinstance(result, dict):
        exp.update(sessionId=result.get("sessionId"),
                   finalResponse=result.get("agentFinalResponse"),
                   status=result.get("status"))
    with open(os.path.join(out, f"{name}.expected.json"), "w", encoding="utf-8", newline="\n") as f:
        json.dump(exp, f, indent=2, ensure_ascii=False)
        f.write("\n")

def main():
    ap = argparse.ArgumentParser(description="Collect agent stdout samples.")
    ap.add_argument("--out", required=True)
    ap.add_argument("--job-dir", action="append", default=[])
    ap.add_argument("--db-export")
    ap.add_argument("--config", required=True)
    ap.add_argument("--max-mb", type=float, default=20)
    a = ap.parse_args()

    os.makedirs(a.out, exist_ok=True)
    agents = load_json(a.config).get("agents", {})
    protocol = lambda aid: (agents.get(aid) or {}).get("outputProtocol")
    max_bytes = a.max_mb * 1024 * 1024
    seen, counts, errors, skipped = set(), Counter(), 0, 0

    for parent in a.job_dir:
        for entry in sorted(os.scandir(parent), key=lambda e: e.name):
            if not entry.is_dir():
                continue
            try:
                log = os.path.join(entry.path, "stdout.log")
                res_p, req_p = (os.path.join(entry.path, n) for n in ("result.json", "request.json"))
                meta_p = res_p if os.path.isfile(res_p) else req_p
                if not os.path.isfile(log) or not os.path.isfile(meta_p):
                    continue
                meta = load_json(meta_p)
                job_id, proto = meta.get("jobId"), protocol(meta.get("agent"))
                if not job_id or proto not in ALLOWED or job_id in seen:
                    skipped += 1
                    continue
                if os.path.getsize(log) > max_bytes:
                    skipped += 1
                    continue
                name = f"dir-{job_id}"
                shutil.copyfile(log, os.path.join(a.out, f"{name}.jsonl"))
                write_expected(a.out, name, proto, meta if meta_p == res_p else None)
                seen.add(job_id)
                counts[("dir", proto)] += 1
            except Exception as e:
                errors += 1
                print(f"error {entry.path}: {e}")

    if a.db_export:
        with open(a.db_export, encoding="utf-8-sig") as f:
            for n, raw in enumerate(f, 1):
                if not raw.strip():
                    continue
                try:
                    rec = json.loads(raw)
                    job_id, proto = rec.get("jobId"), protocol(rec.get("agent"))
                    if not job_id or job_id in seen or proto not in ALLOWED:
                        skipped += 1
                        continue
                    events = [ev for b in rec.get("batches") or [] for ev in b.get("events") or []
                              if ev.get("source") == "agent" and ev.get("type") == "stdout"]
                    events.sort(key=lambda ev: ev.get("sequence", 0))
                    lines = [str(ev.get("content") or "").rstrip("\r\n") for ev in events]
                    name = f"db-{job_id}"
                    with open(os.path.join(a.out, f"{name}.jsonl"), "w", encoding="utf-8", newline="\n") as o:
                        o.write("\n".join(lines) + ("\n" if lines else ""))
                    write_expected(a.out, name, proto, rec.get("result"))
                    seen.add(job_id)
                    counts[("db", proto)] += 1
                except Exception as e:
                    errors += 1
                    print(f"error db line {n}: {e}")

    for (src, proto), c in sorted(counts.items()):
        print(f"{src:4} {proto:20} {c}")
    print(f"total {sum(counts.values())}, skipped {skipped}, errors {errors}")

if __name__ == "__main__":
    main()

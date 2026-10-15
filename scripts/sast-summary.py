#!/usr/bin/env python3
"""Converte a saída JSON do Semgrep no sumário aedis.platform/sast-summary/v1 e aplica o gate: qualquer
achado ERROR não aceito falha. Aceites (sast-accept.yaml) têm rule, path (prefixo), until (AAAA-MM-DD) e reason.

Uso: sast-summary.py --input semgrep.json [--accept sast-accept.yaml] [--out sast-summary.json] [--self-test]
"""
import argparse, json, sys
from datetime import date, datetime, timezone


def load_accepts(path):
    if not path:
        return []
    import yaml  # PyYAML vem com o runner e com a imagem do semgrep
    data = yaml.safe_load(open(path, encoding="utf-8")) or {}
    return [a for a in data.get("accept", []) if date.fromisoformat(str(a["until"])) >= date.today()]


def summarize(results, accepts):
    findings = []
    for r in results:
        sev = r.get("extra", {}).get("severity", "INFO").upper()
        path = r.get("path", "")
        accepted = next((a for a in accepts if a["rule"] == r.get("check_id") and path.startswith(a.get("path", ""))), None)
        findings.append({"rule": r.get("check_id"), "severity": sev, "path": path, "line": r.get("start", {}).get("line"),
                         "message": r.get("extra", {}).get("message", "")[:200],
                         "accepted": accepted["reason"] if accepted else None})
    blocking = [f for f in findings if f["severity"] == "ERROR" and not f["accepted"]]
    counts = {s: sum(1 for f in findings if f["severity"] == s) for s in ("ERROR", "WARNING", "INFO")}
    return {"schema": "aedis.platform/sast-summary/v1",
            "generatedAt": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
            "result": "pass" if not blocking else "fail", "counts": counts,
            "blocking": len(blocking), "accepted": sum(1 for f in findings if f["accepted"]), "findings": findings}


def self_test():
    results = [{"check_id": "r1", "path": "src/a.cs", "start": {"line": 3}, "extra": {"severity": "ERROR", "message": "m"}},
               {"check_id": "r2", "path": "src/b.cs", "start": {"line": 9}, "extra": {"severity": "WARNING", "message": "m"}}]
    assert summarize(results, [])["result"] == "fail"
    assert summarize(results, [{"rule": "r1", "path": "src/", "reason": "x", "until": "2999-01-01"}])["result"] == "pass"
    assert summarize([], [])["result"] == "pass"
    print("self-test ok")


def main():
    a = argparse.ArgumentParser()
    a.add_argument("--input")
    a.add_argument("--accept")
    a.add_argument("--out", default="sast-summary.json")
    a.add_argument("--self-test", action="store_true")
    args = a.parse_args()
    if args.self_test:
        self_test(); return 0
    data = json.load(open(args.input, encoding="utf-8"))
    summary = summarize(data.get("results", []), load_accepts(args.accept))
    json.dump(summary, open(args.out, "w", encoding="utf-8"), indent=2)
    c = summary["counts"]
    print(f"SAST: {c['ERROR']} error, {c['WARNING']} warning, {c['INFO']} info; {summary['accepted']} aceitos; {summary['blocking']} bloqueantes")
    for f in summary["findings"]:
        if f["severity"] == "ERROR" and not f["accepted"]:
            print(f"  {f['path']}:{f['line']} {f['rule']}")
    return 1 if summary["blocking"] else 0


if __name__ == "__main__":
    sys.exit(main())

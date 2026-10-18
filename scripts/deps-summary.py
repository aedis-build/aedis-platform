#!/usr/bin/env python3
"""Converte a saída de `dotnet list package --vulnerable --include-transitive --format json` no sumário
aedis.platform/deps-summary/v1 e aplica o gate: vulnerabilidade High/Critical não aceita falha. Aceites
(deps-accept.yaml) têm package, until (AAAA-MM-DD) e reason.

Uso: deps-summary.py --input deps.json [--accept deps-accept.yaml] [--out deps-summary.json] [--self-test]
"""
import argparse, json, os, sys
from datetime import date, datetime, timezone

BLOCKING = {"High", "Critical"}


def load_accepts(path):
    if not path or not os.path.exists(path):
        return []
    import yaml  # PyYAML vem com o runner
    data = yaml.safe_load(open(path, encoding="utf-8")) or {}
    return [a for a in data.get("accept", []) if date.fromisoformat(str(a["until"])) >= date.today()]


def summarize(report, accepts):
    findings = []
    for project in report.get("projects", []):
        for fw in project.get("frameworks", []) or []:
            for kind in ("topLevelPackages", "transitivePackages"):
                for pkg in fw.get(kind, []) or []:
                    for v in pkg.get("vulnerabilities", []) or []:
                        accepted = next((a for a in accepts if a["package"].lower() == pkg["id"].lower()), None)
                        findings.append({"package": pkg["id"], "version": pkg.get("resolvedVersion"), "severity": v.get("severity", "Unknown"),
                                         "advisory": v.get("advisoryurl"), "transitive": kind == "transitivePackages",
                                         "project": os.path.basename(project.get("path", "")),
                                         "accepted": accepted["reason"] if accepted else None})
    blocking = [f for f in findings if f["severity"] in BLOCKING and not f["accepted"]]
    counts = {s: sum(1 for f in findings if f["severity"] == s) for s in ("Critical", "High", "Moderate", "Low")}
    return {"schema": "aedis.platform/deps-summary/v1",
            "generatedAt": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
            "result": "pass" if not blocking else "fail", "counts": counts,
            "blocking": len(blocking), "accepted": sum(1 for f in findings if f["accepted"]), "findings": findings}


def self_test():
    rep = {"projects": [{"path": "src/A/A.csproj", "frameworks": [{"framework": "net10.0",
           "topLevelPackages": [{"id": "Foo", "resolvedVersion": "1.0", "vulnerabilities": [{"severity": "High", "advisoryurl": "u"}]}],
           "transitivePackages": [{"id": "Bar", "resolvedVersion": "2.0", "vulnerabilities": [{"severity": "Low"}]}]}]}]}
    assert summarize(rep, [])["result"] == "fail"
    assert summarize(rep, [{"package": "foo", "reason": "x", "until": "2999-01-01"}])["result"] == "pass"
    assert summarize({"projects": []}, [])["result"] == "pass"
    print("self-test ok")


def main():
    a = argparse.ArgumentParser()
    a.add_argument("--input")
    a.add_argument("--accept")
    a.add_argument("--out", default="deps-summary.json")
    a.add_argument("--self-test", action="store_true")
    args = a.parse_args()
    if args.self_test:
        self_test(); return 0
    summary = summarize(json.load(open(args.input, encoding="utf-8")), load_accepts(args.accept))
    json.dump(summary, open(args.out, "w", encoding="utf-8"), indent=2)
    c = summary["counts"]
    print(f"deps: {c['Critical']} critical, {c['High']} high, {c['Moderate']} moderate, {c['Low']} low; {summary['accepted']} aceitos; {summary['blocking']} bloqueantes")
    for f in summary["findings"]:
        if f["severity"] in BLOCKING and not f["accepted"]:
            print(f"  {f['package']} {f['version']} [{f['severity']}] {f['advisory']} ({f['project']})")
    return 1 if summary["blocking"] else 0


if __name__ == "__main__":
    sys.exit(main())

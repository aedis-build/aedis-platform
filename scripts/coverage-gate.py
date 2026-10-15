#!/usr/bin/env python3
"""Lê relatórios Cobertura (coverlet) e aplica os limiares de linha/branch. Escreve coverage-summary.json
(contrato aedis.platform/coverage-summary/v1). Sem regressão: compara com um sumário anterior, se informado.

Uso: coverage-gate.py --reports 'TestResults/**/coverage.cobertura.xml' --min-line 60 --min-branch 0
     [--baseline prev/coverage-summary.json] [--out coverage-summary.json]
"""
import argparse, glob, json, sys, xml.etree.ElementTree as ET
from datetime import datetime, timezone


def read(paths):
    lines_valid = lines_covered = branches_valid = branches_covered = 0
    projects = []
    for p in paths:
        root = ET.parse(p).getroot()
        lv, lc = int(root.get("lines-valid", 0)), int(root.get("lines-covered", 0))
        bv, bc = int(root.get("branches-valid", 0)), int(root.get("branches-covered", 0))
        lines_valid += lv; lines_covered += lc; branches_valid += bv; branches_covered += bc
        projects.append({"report": p, "line": pct(lc, lv), "branch": pct(bc, bv)})
    return {"line": pct(lines_covered, lines_valid), "branch": pct(branches_covered, branches_valid),
            "linesValid": lines_valid, "branchesValid": branches_valid, "projects": projects}


def pct(covered, valid):
    return round(100.0 * covered / valid, 2) if valid else 0.0


def main():
    a = argparse.ArgumentParser()
    a.add_argument("--reports", required=True)
    a.add_argument("--min-line", type=float, default=0)
    a.add_argument("--min-branch", type=float, default=0)
    a.add_argument("--baseline")
    a.add_argument("--out", default="coverage-summary.json")
    args = a.parse_args()

    paths = sorted(glob.glob(args.reports, recursive=True))
    if not paths:
        print(f"nenhum relatório em {args.reports}", file=sys.stderr); return 1
    total = read(paths)
    failures = []
    if total["line"] < args.min_line:
        failures.append(f"linhas {total['line']}% < {args.min_line}%")
    if total["branch"] < args.min_branch:
        failures.append(f"branches {total['branch']}% < {args.min_branch}%")
    if args.baseline:
        try:
            base = json.load(open(args.baseline, encoding="utf-8"))["total"]
            if total["line"] + 0.5 < base["line"]:
                failures.append(f"regressão de linhas: {base['line']}% -> {total['line']}%")
        except (OSError, KeyError, ValueError):
            print("baseline ausente ou inválido: regressão não avaliada")

    summary = {"schema": "aedis.platform/coverage-summary/v1",
               "generatedAt": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
               "result": "pass" if not failures else "fail",
               "thresholds": {"minLine": args.min_line, "minBranch": args.min_branch},
               "total": total, "failures": failures}
    json.dump(summary, open(args.out, "w", encoding="utf-8"), indent=2)
    print(f"cobertura: linhas {total['line']}% branches {total['branch']}% ({len(paths)} relatórios)")
    for f in failures:
        print(f"FALHOU: {f}")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())

#!/usr/bin/env python3
"""Gera as notas de uma release a partir dos Conventional Commits entre a tag anterior e a nova: resumo de impacto
da atualização primeiro (quebras de compatibilidade com o texto do BREAKING CHANGE), depois features, correções,
desempenho, dependências, documentação e manutenção, cada item com o link da PR. Saída em Markdown, em inglês,
porque é lida por quem usa o projeto. Atende ao critério release_notes do OpenSSF Best Practices: resumo legível do
que mudou e do impacto, não a saída crua do git log.

Uso: release-notes.py --repo owner/name --tag vX.Y.Z [--range vA.B.C..vX.Y.Z] [--out release-notes.md] [--self-test]
"""
import argparse, re, subprocess, sys

HEADER = re.compile(r"^(?P<type>[a-z]+)(?:\((?P<scope>[^)]*)\))?(?P<bang>!)?: (?P<desc>.+)$")
PR_SUFFIX = re.compile(r"\s*\(#(?P<pr>\d+)\)\s*$")
BREAKING_FOOTER = re.compile(r"^BREAKING[ -]CHANGE:\s*(?P<text>.+)", re.MULTILINE)

SECTIONS = [
    ("feat", "Features"),
    ("fix", "Bug fixes"),
    ("perf", "Performance"),
    ("deps", "Dependencies"),
    ("docs", "Documentation"),
    ("other", "Maintenance"),
]


def parse(subject, body):
    """Classifica um commit; devolve None para assuntos fora do padrão Conventional (merges, reverts manuais)."""
    match = HEADER.match(subject.strip())
    if not match:
        return None
    desc = match.group("desc")
    pr = PR_SUFFIX.search(desc)
    desc = PR_SUFFIX.sub("", desc)
    kind = match.group("type")
    scope = match.group("scope") or ""
    if kind == "build" and scope == "deps":
        section = "deps"
    elif kind in ("feat", "fix", "perf", "docs"):
        section = kind
    else:
        section = "other"
    footer = BREAKING_FOOTER.search(body or "")
    breaking = bool(match.group("bang")) or footer is not None
    return {"section": section, "scope": scope, "desc": desc, "pr": pr.group("pr") if pr else None,
            "breaking": breaking, "breaking_text": footer.group("text").strip() if footer else ""}


def item(entry, repo):
    scope = f"**{entry['scope']}**: " if entry["scope"] else ""
    link = f" ([#{entry['pr']}](https://github.com/{repo}/pull/{entry['pr']}))" if entry["pr"] else ""
    return f"- {scope}{entry['desc']}{link}"


def render(entries, repo, tag, previous):
    breaking = [e for e in entries if e["breaking"]]
    features = sum(1 for e in entries if e["section"] == "feat")
    fixes = sum(1 for e in entries if e["section"] == "fix")
    lines = [f"## {tag}", ""]
    if breaking:
        lines.append(f"**Upgrade impact: breaking.** {len(breaking)} change(s) need action before upgrading; read the section below.")
    elif features:
        lines.append(f"**Upgrade impact: none expected.** Backward-compatible release with {features} new feature(s) and {fixes} fix(es).")
    else:
        lines.append(f"**Upgrade impact: none expected.** Backward-compatible release with {fixes} fix(es) and maintenance.")
    lines.append("")
    if breaking:
        lines += ["### Breaking changes", ""]
        for e in breaking:
            lines.append(item(e, repo))
            if e["breaking_text"]:
                lines.append(f"  - What to do: {e['breaking_text']}")
        lines.append("")
    for key, title in SECTIONS:
        group = [e for e in entries if e["section"] == key and not e["breaking"]]
        if group:
            lines += [f"### {title}", ""] + [item(e, repo) for e in group] + [""]
    if not entries:
        lines += ["No user-facing changes since the previous release.", ""]
    compare = f"https://github.com/{repo}/compare/{previous}...{tag}" if previous else f"https://github.com/{repo}/commits/{tag}"
    lines += ["### Verify this release", "",
              "Packages and the SBOM are signed with Sigstore (`*.sigstore.json`) and carry SLSA build provenance "
              f"(`*.intoto.jsonl`): `gh attestation verify <package> --owner {repo.split('/')[0]}`.", "",
              f"**Full diff**: {compare}", ""]
    return "\n".join(lines)


def git_log(rev_range):
    out = subprocess.run(["git", "log", "--no-merges", "--format=%s%x1f%b%x1e", rev_range],
                         check=True, capture_output=True, text=True, encoding="utf-8").stdout
    commits = []
    for record in out.split("\x1e"):
        if "\x1f" in record:
            subject, body = record.strip("\n").split("\x1f", 1)
            commits.append((subject, body))
    return commits


def self_test():
    commits = [
        ("feat(hosting)!: Swagger leaves the host (#88)", "BREAKING CHANGE: register AddAedisOpenApi in the app host."),
        ("feat(database): MySQL provider (#77)", ""),
        ("fix(hosting): sanitize request path before logging (#86)", ""),
        ("build(deps): bump Npgsql (#89)", ""),
        ("docs: English README (#80)", ""),
        ("ci: single aedis-ci workflow (#90)", ""),
        ("Merge branch 'main' into feature", ""),
    ]
    entries = [e for e in (parse(s, b) for s, b in commits) if e]
    assert len(entries) == 6
    notes = render(entries, "acme/lib", "v1.0.0", "v0.9.0")
    assert "**Upgrade impact: breaking.** 1 change(s)" in notes
    assert "What to do: register AddAedisOpenApi in the app host." in notes
    assert "- **database**: MySQL provider ([#77](https://github.com/acme/lib/pull/77))" in notes
    assert notes.index("### Breaking changes") < notes.index("### Features") < notes.index("### Bug fixes") < notes.index("### Dependencies")
    assert "Swagger leaves the host" not in notes.split("### Features")[1]
    assert "https://github.com/acme/lib/compare/v0.9.0...v1.0.0" in notes
    calm = render([parse("fix: typo (#1)", "")], "acme/lib", "v1.0.1", "v1.0.0")
    assert "**Upgrade impact: none expected.** Backward-compatible release with 1 fix(es)" in calm
    assert "No user-facing changes" in render([], "acme/lib", "v1.0.2", None)
    print("self-test ok")


def main():
    a = argparse.ArgumentParser()
    a.add_argument("--repo")
    a.add_argument("--tag")
    a.add_argument("--range", help="faixa do git log; sem ela, todo o histórico até a tag")
    a.add_argument("--out", default="release-notes.md")
    a.add_argument("--self-test", action="store_true")
    args = a.parse_args()
    if args.self_test:
        self_test()
        return 0
    previous = args.range.split("..")[0] if args.range and ".." in args.range else None
    entries = [e for e in (parse(s, b) for s, b in git_log(args.range or args.tag)) if e]
    notes = render(entries, args.repo, args.tag, previous)
    open(args.out, "w", encoding="utf-8").write(notes)
    print(notes)
    return 0


if __name__ == "__main__":
    sys.exit(main())

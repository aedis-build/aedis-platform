#!/usr/bin/env bash
# Próxima versão semver pelos Conventional Commits desde a última tag: breaking (`tipo!:` ou rodapé BREAKING CHANGE)
# sobe o major, `feat` o minor, `fix`/`perf`/`refactor`/`revert`/`build(deps` o patch; o resto não libera nada. Com
# --bump explícito o incremento é esse, sem ler os commits. Usado pelo workflow reutilizável release-cut.yml.
#
# Uso: next-version.sh --last <tag anterior, vazio na primeira> --bump <auto|patch|minor|major> < log
#      (stdin: `git log --format='%s%n%b'`; stdout: X.Y.Z, ou nada quando não há o que liberar)
#      next-version.sh --self-test
set -euo pipefail

next_version() {
  local last="$1" bump="$2" log="$3"
  if [ "$bump" = auto ]; then
    if echo "$log" | grep -qE '^[a-z]+(\([^)]*\))?!:|^BREAKING CHANGE'; then bump=major
    elif echo "$log" | grep -qE '^feat(\([^)]*\))?:'; then bump=minor
    elif echo "$log" | grep -qE '^(fix|perf|refactor|revert)(\([^)]*\))?:|^build\(deps'; then bump=patch
    else echo "nothing releasable since ${last:-the beginning}" >&2; return 0; fi
  fi
  # A tag pode ter qualquer prefixo (v1.2.3, cli-v1.2.3): a versão começa no primeiro dígito.
  local major minor patch
  IFS=. read -r major minor patch <<< "${last##*[!0-9.]}"
  : "${major:=0}" "${minor:=0}" "${patch:=0}"
  case "$bump" in
    major) major=$((major+1)); minor=0; patch=0 ;;
    minor) minor=$((minor+1)); patch=0 ;;
    patch) patch=$((patch+1)) ;;
    *) echo "unknown bump: $bump" >&2; return 1 ;;
  esac
  echo "$major.$minor.$patch"
}

self_test() {
  local rc=0
  expect() {
    local want="$1" last="$2" bump="$3" log="$4" got
    got="$(next_version "$last" "$bump" "$log" 2>/dev/null)"
    if [ "$got" != "$want" ]; then echo "FAIL last=$last bump=$bump → '$got', want '$want'"; rc=1; fi
  }
  expect 0.1.0 "" auto $'feat(cli): new command'
  expect 1.0.0 "" major ""
  expect 1.2.4 v1.2.3 auto $'fix(x): y\ndocs: z'
  expect 1.2.4 v1.2.3 auto $'build(deps): bump a from 1 to 2'
  expect 1.2.4 v1.2.3 auto $'refactor: tidy'
  expect 1.3.0 v1.2.3 auto $'fix: a\nfeat(scope): b'
  expect 2.0.0 v1.2.3 auto $'feat!: drop x'
  expect 2.0.0 v1.2.3 auto $'feat: a\n\nBREAKING CHANGE: b'
  expect "" v1.2.3 auto $'docs: only\nchore: nothing\nci: x'
  expect 1.2.4 v1.2.3 patch $'docs: only'
  expect 0.1.3 cli-v0.1.2 patch ""
  expect 1.1.0 v1 auto $'feat: a'
  [ $rc -eq 0 ] && echo "self-test ok"
  return $rc
}

last="" bump=auto
case "${1:-}" in
  --self-test) self_test; exit $? ;;
  "") echo "uso: $0 --last <tag> --bump <auto|patch|minor|major> < log | --self-test" >&2; exit 2 ;;
esac
while [ $# -gt 0 ]; do
  case "$1" in
    --last) last="$2"; shift 2 ;;
    --bump) bump="$2"; shift 2 ;;
    *) echo "unknown argument: $1" >&2; exit 2 ;;
  esac
done
next_version "$last" "$bump" "$(cat)"

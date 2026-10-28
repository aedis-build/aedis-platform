#!/usr/bin/env bash
# Verifica que mensagens seguem Conventional Commits (https://www.conventionalcommits.org):
#   <tipo>(<escopo opcional>)!?: <descrição>
# tipos: feat fix docs style refactor perf test build ci chore revert
# Usado pelo workflow conventional-commits.yml (todos os commits da PR + título) e pelo hook commit-msg local.
#
# Uso: check-conventional-commits.sh --range <base>..<head> | --file <arquivo-de-mensagem> | --message "<texto>"
#
# Cópia de compatibilidade dentro da major v1: o conventional-commits.yml que o aedis-ci <= v1.0.x chama resolve a
# plataforma pela tag móvel v1 e espera este caminho. A fonte é scripts/check-conventional-commits.sh da action
# aedis-build/aedis-release-cut; esta cópia sai na v2.
set -euo pipefail
PATTERN='^(feat|fix|docs|style|refactor|perf|test|build|ci|chore|revert)(\([a-z0-9._/-]+\))?!?: [^ ].{0,99}$'

check() {
  local subject="$1"
  case "$subject" in
    "Merge "*|"Revert \""*) return 0 ;;
  esac
  if [[ "$subject" =~ $PATTERN ]]; then return 0; fi
  echo "fora do padrão Conventional Commits: '$subject'" >&2
  echo "  esperado: <tipo>(<escopo>)!: <descrição>   tipos: feat fix docs style refactor perf test build ci chore revert" >&2
  return 1
}

rc=0
case "${1:-}" in
  --range)
    # commits de bots entram por squash com título convencional; só o título é validado para eles
    while IFS=$'	' read -r author subject; do
      [ -n "$subject" ] || continue
      [ "$author" = "dependabot[bot]" ] && continue
      check "$subject" || rc=1
    done < <(git log --no-merges --format='%an%x09%s' "$2")
    ;;
  --file)
    subject="$(grep -v '^#' "$2" | sed -n '1p')"; check "$subject" || rc=1
    ;;
  --message)
    check "$2" || rc=1
    ;;
  *) echo "uso: $0 --range <base>..<head> | --file <msg> | --message <texto>" >&2; exit 2 ;;
esac
[ $rc -eq 0 ] && echo "conventional commits: ok"
exit $rc

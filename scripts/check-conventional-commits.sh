#!/usr/bin/env bash
# Compatibilidade (removido em v2): a regra vive em aedis-build/aedis-release-cut desde a v1.1.0, mas os reutilizáveis
# pinados antes dela (conventional-commits.yml ≤ v1.0.3) ainda executam este caminho no checkout da plataforma, e esse
# checkout cai em `main` quando `github.job_workflow_sha` vem vazio. Este shim busca o script da action, pinado, e o
# executa com os mesmos argumentos.
set -euo pipefail
PIN=08f6b23048839f7ce02f5d9916f884a0d6c5421c # aedis-release-cut, repin to v1.0.0 once released
dir="${RUNNER_TEMP:-${TMPDIR:-/tmp}}/aedis-release-cut-$PIN"
mkdir -p "$dir"
[ -s "$dir/check-conventional-commits.sh" ] || curl -fsSL "https://raw.githubusercontent.com/aedis-build/aedis-release-cut/$PIN/scripts/check-conventional-commits.sh" -o "$dir/check-conventional-commits.sh" \
  || { echo "::error::scripts/check-conventional-commits.sh moved to aedis-build/aedis-release-cut and could not be fetched; pin a platform release >= v1.1.0" >&2; exit 1; }
exec bash "$dir/check-conventional-commits.sh" "$@"

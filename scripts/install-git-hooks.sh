#!/usr/bin/env bash
# Instala o hook commit-msg que valida Conventional Commits localmente (mesma regra do CI). O script vem da action
# Aedis Release Cut (aedis-build/aedis-release-cut), pinado no mesmo commit que o conventional-commits.yml.
set -euo pipefail
PIN=73b343a436d16749aa1da5cf93892adf3b58bfc1 # v1.0.1
# O diretório de hooks é o do repositório principal, também num worktree.
HOOKS="$(git rev-parse --path-format=absolute --git-common-dir)/hooks"
HOOK="$HOOKS/commit-msg"
curl -fsSL "https://raw.githubusercontent.com/aedis-build/aedis-release-cut/$PIN/scripts/check-conventional-commits.sh" -o "$HOOKS/check-conventional-commits.sh"
cat > "$HOOK" <<'EOF'
#!/usr/bin/env bash
exec bash "$(git rev-parse --path-format=absolute --git-common-dir)/hooks/check-conventional-commits.sh" --file "$1"
EOF
chmod +x "$HOOK"
echo "hook commit-msg instalado em $HOOK"

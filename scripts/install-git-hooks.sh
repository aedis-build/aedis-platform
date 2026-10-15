#!/usr/bin/env bash
# Instala o hook commit-msg que valida Conventional Commits localmente (mesma regra do CI).
set -euo pipefail
ROOT="$(git rev-parse --show-toplevel)"
HOOK="$ROOT/.git/hooks/commit-msg"
cat > "$HOOK" <<'EOF'
#!/usr/bin/env bash
exec "$(git rev-parse --show-toplevel)/scripts/check-conventional-commits.sh" --file "$1"
EOF
chmod +x "$HOOK"
echo "hook commit-msg instalado em $HOOK"

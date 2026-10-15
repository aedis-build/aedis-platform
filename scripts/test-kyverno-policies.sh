#!/usr/bin/env bash
# Testa as políticas Kyverno do chart aedis-cluster com a CLI do Kyverno: os renders dos exemplos do chart
# aedis-app passam sem falha; um pod fora do baseline falha em todas as regras esperadas.
# Uso: scripts/test-kyverno-policies.sh   (precisa de helm e kyverno no PATH)
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUT="$(mktemp -d)"; trap 'rm -rf "$OUT"' EXIT

helm template c "$ROOT/charts/aedis-cluster" -f "$ROOT/charts/aedis-cluster/ci/policies-values.yaml" > "$OUT/policies.yaml"
rc=0
for f in "$ROOT"/charts/aedis-app/ci/*.yaml; do
  n="$(basename "$f" .yaml)"
  helm template "$n" "$ROOT/charts/aedis-app" -f "$f" > "$OUT/$n.yaml"
  summary="$(kyverno apply "$OUT/policies.yaml" --resource "$OUT/$n.yaml" 2>&1 | grep -E '^pass:' || true)"
  printf "%-28s %s\n" "$n" "$summary"
  echo "$summary" | grep -qE 'fail: 0,' || { echo "   ERRO: render de $n reprovado pelas políticas"; kyverno apply "$OUT/policies.yaml" --resource "$OUT/$n.yaml" 2>&1 | grep -A3 'failed:'; rc=1; }
done

cat > "$OUT/bad-pod.yaml" <<'EOF'
apiVersion: v1
kind: Pod
metadata:
  name: bad
  namespace: shop
spec:
  containers:
    - name: app
      image: nginx:latest
EOF
bad="$(kyverno apply "$OUT/policies.yaml" --resource "$OUT/bad-pod.yaml" 2>&1 || true)"
for rule in pod-security-restricted read-only-root-filesystem requests-and-limits disallow-latest-tag; do
  echo "$bad" | grep -q "$rule" && echo "pod ruim: $rule reprovou (ok)" || { echo "ERRO: pod ruim passou em $rule"; rc=1; }
done
[ $rc -eq 0 ] && echo "políticas OK" || echo "FALHOU"
exit $rc

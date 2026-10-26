#!/usr/bin/env bash
# Instancia o esqueleto do repo GitOps do tenant (template/tenant) substituindo os tokens.
# Equivalente ao `aedis platform init` da CLI, para uso em CI e sem .NET.
#
# Uso: render-tenant-template.sh --cloud aws|azure|digitalocean|generic --org <org-github> [--envs dev,prod]
#      [--platform-ref "<sha> # vX.Y.Z"] [--chart oci://ghcr.io/aedis-build/charts/aedis-app] [--chart-version 0.1.0] --out <dir>
set -euo pipefail
CLOUD="generic"; ORG=""; ENVS="dev,prod"; REF="v1"; CHART="oci://ghcr.io/aedis-build/charts/aedis-app"; CHART_VERSION="0.1.0"; OUT=""
while [ $# -gt 0 ]; do
  case "$1" in
    --cloud) CLOUD="$2"; shift 2 ;;
    --org) ORG="$2"; shift 2 ;;
    --envs) ENVS="$2"; shift 2 ;;
    --platform-ref) REF="$2"; shift 2 ;;
    --chart) CHART="$2"; shift 2 ;;
    --chart-version) CHART_VERSION="$2"; shift 2 ;;
    --out) OUT="$2"; shift 2 ;;
    *) echo "argumento desconhecido: $1" >&2; exit 2 ;;
  esac
done
[ -n "$ORG" ] && [ -n "$OUT" ] || { echo "--org e --out são obrigatórios" >&2; exit 2; }
case "$CLOUD" in aws|azure|digitalocean|generic) ;; *) echo "cloud inválido: $CLOUD" >&2; exit 2 ;; esac

SRC="$(cd "$(dirname "$0")/../template/tenant" && pwd)"
mkdir -p "$OUT"
cp -r "$SRC"/. "$OUT"/

case "$CLOUD" in
  aws) SECRETS_PROVIDER=aws; INGRESS_PRESET=alb ;;
  azure) SECRETS_PROVIDER=azure; INGRESS_PRESET=agic ;;
  digitalocean) SECRETS_PROVIDER=vault; INGRESS_PRESET=do-nginx ;;
  *) SECRETS_PROVIDER=vault; INGRESS_PRESET=nginx ;;
esac

# platform/values/<env>.yaml a partir do modelo, um por ambiente
IFS=, read -r -a envs <<< "$ENVS"
mkdir -p "$OUT/platform/values"
for env in "${envs[@]}"; do
  class=nonprod; [ "$env" = "prod" ] || [ "$env" = "production" ] || [ "$env" = "prd" ] && class=prod
  sed -e "s/__AEDIS_ENV__/$env/g" -e "s/__AEDIS_ENV_CLASS__/$class/g" \
      "$OUT/platform/values/_env.yaml.tmpl" > "$OUT/platform/values/$env.yaml"
done
rm -f "$OUT/platform/values/_env.yaml.tmpl"

# tokens globais
find "$OUT" -type f \( -name '*.yaml' -o -name '*.yml' -o -name '*.md' -o -name 'CODEOWNERS' \) -print0 | xargs -0 sed -i \
  -e "s|__AEDIS_CLOUD__|$CLOUD|g" \
  -e "s|__AEDIS_ORG__|$ORG|g" \
  -e "s|__AEDIS_PLATFORM_REF__|$REF|g" -e "s|__AEDIS_PLATFORM_PIN__|$REF|g" \
  -e "s|__AEDIS_CHART__|$CHART|g" \
  -e "s|__AEDIS_CHART_VERSION__|$CHART_VERSION|g" \
  -e "s|__AEDIS_SECRETS_PROVIDER__|$SECRETS_PROVIDER|g" \
  -e "s|__AEDIS_INGRESS_PRESET__|$INGRESS_PRESET|g"

echo "repo do tenant gerado em $OUT (cloud=$CLOUD, envs=$ENVS)"

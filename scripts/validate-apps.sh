#!/usr/bin/env bash
# Valida um repo GitOps do Aedis Platform: para cada apps/<app>/env/<env>.yaml renderiza o chart com as
# três camadas (platform/values/<env>.yaml -> app.yaml -> env/<env>.yaml), confere nomes, roda kubeconform
# (quando instalado) e escreve os manifestos renderizados + validate-summary.json.
#
# Uso: validate-apps.sh --chart <dir ou oci://...> [--chart-version X] [--repo <dir>] [--out <dir>] [--k8s 1.31.0]
set -euo pipefail

CHART=""; CHART_VERSION=""; REPO="."; OUT=""; K8S="1.31.0"
while [ $# -gt 0 ]; do
  case "$1" in
    --chart) CHART="$2"; shift 2 ;;
    --chart-version) CHART_VERSION="$2"; shift 2 ;;
    --repo) REPO="$2"; shift 2 ;;
    --out) OUT="$2"; shift 2 ;;
    --k8s) K8S="$2"; shift 2 ;;
    *) echo "argumento desconhecido: $1" >&2; exit 2 ;;
  esac
done
[ -n "$CHART" ] || { echo "--chart é obrigatório" >&2; exit 2; }
cd "$REPO"
[ -n "$OUT" ] || OUT="$(mktemp -d)"
mkdir -p "$OUT"

chart_args=("$CHART")
[ -n "$CHART_VERSION" ] && chart_args+=(--version "$CHART_VERSION")

rc=0
checks=()
add_check() { checks+=("{\"app\":\"$1\",\"environment\":\"$2\",\"check\":\"$3\",\"status\":\"$4\",\"detail\":$(printf '%s' "$5" | python3 -c 'import json,sys; print(json.dumps(sys.stdin.read()))')}"); }

shopt -s nullglob
apps=(apps/*/app.yaml)
[ ${#apps[@]} -gt 0 ] || { echo "nenhum apps/<app>/app.yaml encontrado em $(pwd)"; exit 1; }

for app_file in "${apps[@]}"; do
  app_dir="$(dirname "$app_file")"; app="$(basename "$app_dir")"
  name="$(sed -n 's/^name: *//p' "$app_file" | tr -d '"'"'" | head -n1)"
  if [ "$name" != "$app" ]; then
    echo "ERRO: $app_file tem name=$name (esperado $app, o nome do diretório)"; rc=1
    add_check "$app" "-" "name-matches-directory" "fail" "name=$name"
  fi
  envs=("$app_dir"/env/*.yaml)
  [ ${#envs[@]} -gt 0 ] || { echo "ERRO: $app sem env/<env>.yaml"; rc=1; add_check "$app" "-" "has-environment" "fail" "nenhum env/*.yaml"; continue; }
  for env_file in "${envs[@]}"; do
    env="$(basename "$env_file" .yaml)"
    echo "== $app ($env)"
    values=()
    if [ -f "platform/values/$env.yaml" ]; then values+=(-f "platform/values/$env.yaml"); else echo "   aviso: platform/values/$env.yaml não existe"; add_check "$app" "$env" "platform-values" "warn" "platform/values/$env.yaml ausente"; fi
    values+=(-f "$app_file" -f "$env_file")
    out="$OUT/$app-$env.yaml"
    if ! helm template "$app" "${chart_args[@]}" "${values[@]}" > "$out" 2> "$out.err"; then
      echo "   ERRO: helm template falhou:"; sed 's/^/     /' "$out.err"; rc=1
      add_check "$app" "$env" "helm-template" "fail" "$(cat "$out.err")"; continue
    fi
    add_check "$app" "$env" "helm-template" "pass" ""
    env_in_file="$(sed -n 's/^environment: *//p' "$env_file" | tr -d '"'"'" | head -n1)"
    if [ -n "$env_in_file" ] && [ "$env_in_file" != "$env" ]; then
      echo "   ERRO: environment=$env_in_file em $env_file difere do nome do arquivo"; rc=1
      add_check "$app" "$env" "environment-matches-file" "fail" "environment=$env_in_file"
    fi
    if command -v kubeconform >/dev/null 2>&1; then
      if kubeconform -strict -summary -kubernetes-version "$K8S" \
          -schema-location default \
          -schema-location 'https://raw.githubusercontent.com/datreeio/CRDs-catalog/main/{{.Group}}/{{.ResourceKind}}_{{.ResourceAPIVersion}}.json' \
          "$out" > "$out.kubeconform" 2>&1; then
        add_check "$app" "$env" "kubeconform" "pass" ""
      else
        cat "$out.kubeconform"; rc=1; add_check "$app" "$env" "kubeconform" "fail" "$(cat "$out.kubeconform")"
      fi
    fi
  done
done

{
  echo "{\"schema\":\"aedis.platform/validate-summary/v1\",\"generatedAt\":\"$(date -u +%Y-%m-%dT%H:%M:%SZ)\",\"result\":\"$([ $rc -eq 0 ] && echo pass || echo fail)\",\"checks\":["
  IFS=,; echo "${checks[*]}"; unset IFS
  echo "]}"
} > "$OUT/validate-summary.json"

echo "manifestos e sumário em $OUT"
[ $rc -eq 0 ] && echo "OK: todos os apps válidos" || echo "FALHOU"
exit $rc

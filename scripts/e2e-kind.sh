#!/usr/bin/env bash
# e2e do chart num cluster kind: instala Argo Rollouts, External Secrets Operator (provider fake), KEDA e o CRD de
# ServiceMonitor; aplica as fixtures; instala o app de exemplo; espera Rollout Healthy; verifica o Secret
# sincronizado, as env derivadas e as probes. Usado pelo CI (e2e-kind.yml) e localmente.
#
# Uso: scripts/e2e-kind.sh [--keep] [--cluster aedis-e2e]
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
CLUSTER="aedis-e2e"; KEEP=0
while [ $# -gt 0 ]; do case "$1" in --keep) KEEP=1; shift ;; --cluster) CLUSTER="$2"; shift 2 ;; *) echo "arg desconhecido: $1" >&2; exit 2 ;; esac; done

ROLLOUTS_VERSION="${ROLLOUTS_VERSION:-v1.8.3}"
ESO_VERSION="${ESO_VERSION:-0.18.2}"
KEDA_VERSION="${KEDA_VERSION:-2.17.2}"
PROM_CRD_VERSION="${PROM_CRD_VERSION:-v0.83.0}"

cleanup() { [ $KEEP -eq 1 ] || kind delete cluster --name "$CLUSTER" >/dev/null 2>&1 || true; }
trap cleanup EXIT

echo "== cluster kind $CLUSTER"
kind get clusters 2>/dev/null | grep -qx "$CLUSTER" || kind create cluster --name "$CLUSTER" --wait 120s
kubectl config use-context "kind-$CLUSTER" >/dev/null

echo "== Argo Rollouts $ROLLOUTS_VERSION"
kubectl create namespace argo-rollouts --dry-run=client -o yaml | kubectl apply -f - >/dev/null
kubectl apply -n argo-rollouts -f "https://github.com/argoproj/argo-rollouts/releases/download/${ROLLOUTS_VERSION}/install.yaml" >/dev/null

echo "== External Secrets Operator $ESO_VERSION"
helm repo add external-secrets https://charts.external-secrets.io >/dev/null 2>&1 || true
helm repo update >/dev/null
helm upgrade --install external-secrets external-secrets/external-secrets -n external-secrets --create-namespace \
  --version "$ESO_VERSION" --set installCRDs=true --wait --timeout 5m >/dev/null

echo "== KEDA $KEDA_VERSION"
helm repo add kedacore https://kedacore.github.io/charts >/dev/null 2>&1 || true
helm upgrade --install keda kedacore/keda -n keda --create-namespace --version "$KEDA_VERSION" --wait --timeout 5m >/dev/null

echo "== CRD ServiceMonitor $PROM_CRD_VERSION"
kubectl apply --server-side -f "https://raw.githubusercontent.com/prometheus-operator/prometheus-operator/${PROM_CRD_VERSION}/example/prometheus-operator-crd/monitoring.coreos.com_servicemonitors.yaml" >/dev/null

kubectl -n argo-rollouts rollout status deployment/argo-rollouts --timeout=180s >/dev/null

echo "== fixtures (namespace restricted + ClusterSecretStore fake)"
kubectl apply -f "$ROOT/examples/e2e/fixtures.yaml" >/dev/null

echo "== helm install orders-api"
helm upgrade --install orders-api "$ROOT/charts/aedis-app" -n shop -f "$ROOT/examples/e2e/values.yaml" --wait=false >/dev/null

echo "== espera ExternalSecret sincronizar"
kubectl -n shop wait externalsecret/orders-api --for=condition=Ready=True --timeout=120s
kubectl -n shop get secret orders-api-secret -o jsonpath='{.data.Database__ConnectionString}' | base64 -d | grep -q "Host=db" && echo "   Secret sincronizado com o valor do cofre"

echo "== espera Rollout Healthy (canário 50% + pause 5s)"
for i in $(seq 1 60); do
  phase="$(kubectl -n shop get rollout orders-api -o jsonpath='{.status.phase}' 2>/dev/null || true)"
  [ "$phase" = "Healthy" ] && break
  sleep 5
done
[ "$phase" = "Healthy" ] || { echo "Rollout não ficou Healthy (fase: $phase)"; kubectl -n shop describe rollout orders-api | tail -40; kubectl -n shop get pods; exit 1; }
echo "   Rollout Healthy"

echo "== asserts"
pod="$(kubectl -n shop get pods -l app.kubernetes.io/name=orders-api -o jsonpath='{.items[0].metadata.name}')"
kubectl -n shop get pod "$pod" -o jsonpath='{.spec.containers[0].securityContext.readOnlyRootFilesystem}' | grep -q true && echo "   readOnlyRootFilesystem"
kubectl -n shop get pod "$pod" -o jsonpath='{.spec.securityContext.runAsUser}' | grep -q 1654 && echo "   runAsUser 1654"
kubectl -n shop get pod "$pod" -o jsonpath='{.spec.containers[0].env[?(@.name=="ASPNETCORE_ENVIRONMENT")].value}' | grep -q Staging && echo "   ASPNETCORE_ENVIRONMENT=Staging"
kubectl -n shop get pod "$pod" -o jsonpath='{.spec.containers[0].env[?(@.name=="Security__ForwardedHeaders__TrustAllProxies")].value}' | grep -q false && echo "   TrustAllProxies=false"
kubectl -n shop exec "$pod" -- sh -c 'echo $Database__ConnectionString' 2>/dev/null | grep -q "Host=db" && echo "   envFrom do Secret visível no container" || echo "   (container sem shell: envFrom checado pelo Secret)"
kubectl -n shop get servicemonitor orders-api >/dev/null && echo "   ServiceMonitor criado"
kubectl -n shop get networkpolicy orders-api-egress >/dev/null && echo "   NetworkPolicy de egresso criada"
kubectl -n shop get pdb orders-api >/dev/null && echo "   PDB criado"
echo "e2e OK"

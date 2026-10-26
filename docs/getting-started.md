# Começando

Este guia leva uma aplicação Aedis do repositório até um cluster Kubernetes com o fluxo GitOps do Aedis
Platform. Serve para AWS (EKS), Azure (AKS), DigitalOcean (DOKS) e qualquer Kubernetes (`generic`).

## 1. Pré-requisitos no cluster

| Componente | Para quê | Obrigatório |
|---|---|---|
| ArgoCD | lê o repo GitOps e aplica o chart | sim |
| Argo Rollouts | canário com análise (`rollout.strategy: canary`) | não (`rolling` usa Deployment) |
| External Secrets Operator + `ClusterSecretStore aedis-secrets` | sincroniza o cofre para `Secret`s | sim (se o app tem segredos) |
| KEDA | escala por fila e horário | não |
| Prometheus Operator (ou o backend do preset) | ServiceMonitor e análise do canário | não |
| Ingress controller do preset (nginx, ALB, AGIC, Kong) | expor APIs | não |
| Kyverno | políticas do nível L2+ | não (L1) |

O `ClusterSecretStore` aponta para o cofre da nuvem com a identidade do próprio ESO. Exemplos por cofre em
`charts/aedis-cluster` (values `secretStore.*`) e nos guias de nuvem.

## 2. Criar o repositório GitOps

```bash
aedis platform init --cloud aws --org minha-org --envs dev,prod --out gitops-apps
# sem .NET:
scripts/render-tenant-template.sh --cloud aws --org minha-org --envs dev,prod --out gitops-apps
```

Preencha `platform/values/<env>.yaml` com os fatos do ambiente: região, prefixo do cofre, CIDR da VPC/VNet
(`ingress.proxyNetworks`), endpoint do OpenTelemetry Collector, preset de ingress.

Instale o `argocd/applicationset.yaml` no ArgoCD e registre cada cluster com a label
`aedis.build/environment=<env>`.

## 3. Declarar um app

```bash
aedis platform new-app orders-api --type api --namespace shop
```

Gera `apps/orders-api/app.yaml` e `apps/orders-api/env/<env>.yaml`. O mínimo:

```yaml
# apps/orders-api/app.yaml
name: orders-api
namespace: shop
type: api
image:
  repository: ghcr.io/minha-org/orders-api
secrets:
  keys:
    - name: Database__ConnectionString
      key: orders-api            # segredo no cofre, sem o prefixo do ambiente
      property: ConnectionString # campo dentro do segredo
ingress:
  enabled: true
  hosts: [orders.dev.minha-org.com]
  edge:
    waf: false
```

```yaml
# apps/orders-api/env/dev.yaml
environment: dev
image:
  tag: 0.1.0-rc.1
```

Em produção a imagem é referenciada por digest (`image.digest`), escrito pelo PR de promoção.

## 4. Validar

```bash
aedis platform validate        # helm template + schema + kubeconform + sumário JSON
```

O mesmo roda no PR pelo workflow `validate.yml` do tenant (`uses: aedis-build/aedis-platform/.github/workflows/validate.yml@<sha> # vX.Y.Z`, veja abaixo).

## Pinar os workflows da plataforma

Todo `uses:` de um workflow ou action desta plataforma aponta para o **SHA do commit de uma tag de release**
`vX.Y.Z`, com a tag no comentário, e nunca para um commit de branch:

```yaml
    uses: aedis-build/aedis-platform/.github/workflows/aedis-ci.yml@<sha da tag> # v1.0.0
```

As tags saem do `release.yml` desta plataforma a cada push em `main` que libera algo (Conventional Commits). Com o
comentário `# vX.Y.Z`, o Dependabot (`package-ecosystem: github-actions`) abre o pull request de repin a cada release,
SHA e comentário juntos; um pin em `main` ou numa branch de PR nunca é atualizado e some quando a branch é apagada.
O input `platform-ref` dos reutilizáveis é opcional: vazio, os scripts vêm do mesmo commit do workflow pinado.

## 5. O que o chart faz pela aplicação Aedis

Com `framework.enabled: true` (padrão) o chart injeta no container:

| Variável | Valor |
|---|---|
| `ASPNETCORE_ENVIRONMENT` / `DOTNET_ENVIRONMENT` | `Staging` em `nonprod`, `Production` em `prod` |
| `ASPNETCORE_URLS` | `http://+:<container.port>` |
| `OTEL_SERVICE_NAME`, `OTEL_RESOURCE_ATTRIBUTES`, `OTEL_EXPORTER_OTLP_*`, `Telemetry__*` | de `observability.otlp` |
| `Security__ForwardedHeaders__TrustAllProxies=false` + `KnownNetworks__N` | de `ingress.proxyNetworks` |
| `Security__HostHeaders__AllowedHosts__N` | hosts do ingress + nome do Service |
| `Security__Https__EnableHttpsRedirection=false`, `EnableHsts` | TLS termina na borda; HSTS só em prod |

Os segredos chegam por `envFrom` do `Secret <name>-secret`; a chave `Database__ConnectionString` vira
`Database:ConnectionString` na configuração do .NET.

## Próximos passos

- [Referência do chart](chart.md)
- [Sites Next.js com Aedis UI](sites.md): `aedis platform new-site`, `aedis-ci` com `language: node`, `type: site`
- Guias por nuvem: [AWS](clouds/aws.md), [Azure](clouds/azure.md), [DigitalOcean e genérico](clouds/generic.md)
- [Níveis de conformidade](conformance.md)

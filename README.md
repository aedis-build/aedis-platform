# Aedis Platform

Plataforma de entrega **segura e agnóstica de nuvem** (AWS, Azure, DigitalOcean, qualquer Kubernetes) para
aplicações construídas com o framework [Aedis](https://github.com/aedis-build/aedis): um chart com baseline
de segurança sempre ligado, GitOps com ArgoCD e Argo Rollouts, segredos pelo External Secrets Operator,
gates de supply chain e uma régua de conformidade mensurável (OpenSSF Scorecard, OpenSSF Best Practices,
cobertura de código) em três níveis.

> Estado: **preview** (chart `aedis-app` 0.x). Veja [docs/getting-started.md](docs/getting-started.md).

## O que tem aqui

| Diretório | Conteúdo |
|---|---|
| `charts/aedis-app` | Chart Helm para `api`, `worker` e `cronjob`: Rollout canário com análise, ExternalSecret por cofre, identidade de workload (IRSA / Azure Workload Identity), KEDA/HPA, PDB, NetworkPolicy anti-SSRF, presets de ingress (nginx, ALB, AGIC, Kong) e de observabilidade (Datadog, Dynatrace, Splunk, New Relic), tudo opcional e desligado por padrão |
| `template/tenant` | Esqueleto do repositório GitOps do cliente (`platform/values`, `apps/`, ApplicationSet, workflows) |
| `scripts/` | `validate-apps.sh` (render + schema + kubeconform + sumário JSON), `render-tenant-template.sh` |
| `.github/workflows` | Workflows reutilizáveis (`validate.yml`; em seguida `build-image`, `sast`, `dast`, `promote`, `scorecard`, `coverage`) |
| `examples/` | App de exemplo e repo tenant de exemplo usados pelo CI |
| `docs/` | Guias por nuvem, referência do chart, controles de segurança e níveis de conformidade |

## Como funciona

```
repo da app ──(app-ci: build, testes, cobertura, SAST, imagem assinada + SBOM)──▶ registry
                                                                                    │
repo GitOps ◀──(argocd-image-updater escreve image.tag em env/<não-prod>.yaml)──────┘
    │
    ├─ platform/values/<env>.yaml   fatos do cluster (nuvem, cofre, ingress, rede, OTLP)
    ├─ apps/<app>/app.yaml          declaração do app
    └─ apps/<app>/env/<env>.yaml    imagem e ajustes por ambiente
    │
ArgoCD (ApplicationSet) ──▶ chart aedis-app ──▶ Rollout canário + análise ──▶ Synced/Healthy
    │
    └─ promote.yml: SAST/DAST verdes ──▶ PR em env/prod.yaml com tag + digest ──▶ aprovação ──▶ sync manual
```

Os segredos nunca passam pelo git: o chart gera `ExternalSecret`s que o ESO sincroniza do AWS Secrets
Manager, do Azure Key Vault ou do HashiCorp Vault para um `Secret` consumido por `envFrom`. O chart também
deriva as variáveis que configuram o host Aedis (ambiente, OTLP, proxies confiáveis, hosts permitidos),
então a aplicação não conhece o cluster.

## Começar

```bash
dotnet tool install -g aedis-cli            # em breve
aedis platform init --cloud aws --org minha-org --envs dev,prod
aedis platform new-app orders-api --type api --namespace shop
aedis platform validate --level 1
```

Sem .NET: `scripts/render-tenant-template.sh` e `scripts/validate-apps.sh` fazem o mesmo.

## Níveis de conformidade

| Nível | Resumo |
|---|---|
| **L1 Baseline** | baseline de pod, schema, ESO, probes, PDB; Scorecard publicado; cobertura ≥ 60 % |
| **L2 Hardened** | imagem assinada e pinada por digest, SBOM, Kyverno enforce, NetworkPolicy, PSA restricted, gate SAST; Scorecard ≥ 7; Best Practices silver; cobertura ≥ 75 % |
| **L3 Regulated** | gate DAST, attestation verificada, prod só por PR, drift report, WAF declarado, evidências retidas; Scorecard ≥ 8; Best Practices gold; cobertura ≥ 85 % |

Os níveis são **controles implementados e verificáveis**, não certificação. Detalhes em `docs/conformance.md`.

## Licença

Apache-2.0. Veja [LICENSE](LICENSE) e [SECURITY.md](SECURITY.md).

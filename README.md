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
| `template/site` | Site Next.js com Aedis UI (`aedis platform new-site`): Dockerfile distroless, `proxy.ts` com os cabeçalhos de segurança, CI do `aedis-ci` com `language: node` e `release.yml` da imagem, `type: site` no chart |
| `scripts/` | `validate-apps.sh` (render + schema + kubeconform + sumário JSON), `render-tenant-template.sh` |
| `.github/workflows` | Workflows reutilizáveis (`validate`, `build-image`, `sast`, `scorecard`, `coverage`, `aedis-ci`; em seguida `promote`) |
| `.github/actions/dast` | Ação composta: ZAP baseline contra a URL alvo, sumário `dast-summary/v1` e gate com aceites de risco com validade |
| `examples/` | App de exemplo e repo tenant de exemplo usados pelo CI |
| `docs/` | Guias por nuvem, referência do chart, controles de segurança e níveis de conformidade |

## Como funciona

```
repo da app ──(aedis-ci: build, testes, cobertura, SAST, CodeQL, REUSE, DAST; release: imagem assinada + SBOM)──▶ registry
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
| **L3 Regulated** | gate DAST, attestation verificada, prod só por PR, drift report, WAF declarado, evidências retidas; Scorecard ≥ 9 (todo check sob controle do processo em 10); Best Practices gold; cobertura ≥ 85 % |

Os níveis são **controles implementados e verificáveis**, não certificação. Detalhes em `docs/conformance.md`.

## Portão de merge (`aedis pr gate`)

Fecha o ciclo sem bypass de administrador: um workflow `workflow_run` (sempre a partir do branch padrão, sem
checkout do código da PR) chama `merge-gate.yml`, que avalia a PR contra `.github/aedis-merge-policy.yaml` (autores
confiáveis e sem fork, título Conventional sem trailers de co-autoria de IA, nada de breaking, nenhum caminho que
exija humano, tamanho, estado mergeável, checks obrigatórios verdes no head, sumários `coverage`/`sast` com `pass`
e cobertura sem regressão) e, quando tudo passa, aprova como a GitHub App "Aedis Compliance" e habilita o auto-merge.
A App precisa estar instalada na organização com `pull_requests: write`, `contents: write`, `checks: read`,
`metadata: read`; os secrets `AEDIS_APP_ID` e `AEDIS_APP_PRIVATE_KEY` ficam na organização. Sem eles o portão só
avalia e comenta. O `GITHUB_TOKEN` nunca aprova (ORG-02). No L3 a revisão de code owner continua humana: a App
não é code owner, de propósito.

## Licença

Apache-2.0. Veja [LICENSE](LICENSE) e [SECURITY.md](SECURITY.md).

## Auditoria da organização (`aedis org audit`)

Aplica a régua de conformidade a uma organização GitHub e aos seus repositórios: critérios do selo OpenSSF Best
Practices por tier (`conformance/best-practices.yaml`), controles da organização (seção `organization` do mesmo
arquivo: permissões do Actions, 2FA, Dependabot/secret scanning para repositórios novos, ruleset de organização),
proteção viva do branch padrão e mínimos do OpenSSF Scorecard (`conformance/levels.yaml`). Gera, em inglês,
`org-audit-summary.json` (schema `aedis.platform/org-audit/v1`), `org-audit.md` e `org-audit.html` (relatório
autocontido no estilo dos scanners, com a organização, todos os critérios por repositório, checks obrigatórios e notas
do Scorecard por check). Com `--apply` aplica os guardrails do nível em toda a organização de uma vez: ruleset de
organização `aedis-main` para o branch padrão de todos os repositórios, permissões de workflow do Actions (token só
leitura, sem aprovar PRs), configurações de segurança da org e o ruleset de cada repositório com os checks informados.
2FA não tem API de escrita e segue como item a ligar na UI da organização.

A régua pede evidência, não só configuração: assets da última release (assinaturas Sigstore, SBOM), selo registrado
em bestpractices.dev e o sumário JSON da run mais recente do branch padrão (`coverage-summary`, `sast-summary`,
`dast-summary`) com `result: pass`. Cada item carrega as categorias do OSPS Baseline que cobre. O comando também grava
`badge.json` e `badges/<repo>.json` no formato endpoint do shields.io
(`https://img.shields.io/endpoint?url=<url do json>`) para o README mostrar o nível alcançado. O `coverage.yml`
também publica `deps-summary.json` (`aedis.platform/deps-summary/v1`: vulnerabilidades conhecidas das dependências,
gate em High/Critical com aceites em `deps-accept.yaml`), e o `build-image.yml` emite a atestação de proveniência do
GitHub e a verifica com `gh attestation verify` antes de declarar sucesso.

```bash
aedis org audit --org minha-org --level 3 --out ./auditoria
aedis org audit --org minha-org --level 3 --apply --required-check "ci / build"
```

Autenticação: `GITHUB_TOKEN`/`GH_TOKEN` ou a sessão do `gh`. Com `--apply`, o token precisa de administração
dos repositórios e `--required-check` é obrigatório: informe só checks que rodam em toda PR (um job com filtro de
`paths` como check obrigatório trava o branch); sem a opção o comando lista os checks verdes de cada repositório e sai com 2. Por padrão o papel de administrador pode contornar o ruleset via PR (fica auditável); use
`--no-admin-bypass` quando houver dois ou mais mantenedores.

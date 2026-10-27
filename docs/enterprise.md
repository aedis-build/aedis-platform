# Perfil enterprise

A plataforma nasceu para repositórios públicos no github.com: Scorecard pela API pública, selo OpenSSF Best
Practices, nuget.org e npm por Trusted Publishing, `main` única com `release-cut` automático, um projeto por
repositório, deploy em Cloudflare ou em Kubernetes com GitOps. Este documento descreve o que muda quando a mesma
plataforma roda dentro de uma empresa no **GitHub Enterprise Cloud** (ainda no github.com, repositórios privados,
monorepos .NET e Node) e o plano, uma issue por entrega, para fechar a diferença.

O princípio não muda: **a empresa consome a plataforma publicada, sem fork**. Os workflows reutilizáveis e as
actions vêm de `aedis-build/aedis-platform`, pinados no SHA de uma tag de release ([versioning.md](versioning.md),
PLT-22). Cada entrega abaixo é uma **entrada ou um modo** numa reusable existente, ou uma reusable nova no mesmo
repositório; nunca uma cópia ajustada no repositório da empresa. O que a empresa escreve é o caller: o `uses:` com o
SHA, as entradas e, quando a ferramenta é dela (Sonar), o job que a roda.

## O que muda

| Tema | Público (github.com) | Enterprise (GHEC, privado) |
|---|---|---|
| Régua base | Scorecard publicado, selo Best Practices, cobertura | só cobertura; controles PLT iguais (perfil `internal`) |
| Pacotes | nuget.org e npm por OIDC Trusted Publishing, `--provenance` | GitHub Packages (NuGet e npm) com `GITHUB_TOKEN`; prova de origem é a atestação de artefato do GitHub |
| Branches e release | `main` única; `release-cut` a cada push; tag imediata | `develop → dev`, `main → qas`, tag → `prod`; `release-cut` só na `main`, por `workflow_dispatch`; tag sob demanda, aprovada em environment; canais `rc`/`preview` antes da estável |
| Imagens | GHCR; o mesmo digest promovido entre ambientes | GHCR ou Azure Container Registry; promoção do mesmo digest por padrão, rebuild por ambiente como modo suportado com reprodutibilidade verificada |
| Repositório | um projeto na raiz | monorepo: matriz por caminho alterado e `working-directory` |
| SAST | Semgrep + regras Aedis em `ubuntu-latest` | SonarQube on-premise com action própria; só esse job em runner self-hosted |
| DAST | ZAP baseline por `dast-start` | o mesmo; a empresa não tem DAST próprio, a action serve, com o padrão por tipo de app documentado |
| Deploy | Cloudflare (sites) ou Kubernetes com ArgoCD | Kubernetes para front e back (`type: ssr` para o Node SSR) e Azure Container Apps |
| Runners | `ubuntu-latest` | `ubuntu-latest` em tudo; a exceção única é o job do Sonar, que precisa alcançar a rede on-premise |

Não há espelho interno de actions nem `runs-on` geral: a decisão do dono é não depender de poucos hosts, então só
o job que não tem como rodar fora (Sonar) sai do `ubuntu-latest`.

## Perfil de conformidade `internal`

Os níveis L1 a L3 de [conformance.md](conformance.md) somam uma régua base externa (Scorecard, Best Practices,
cobertura) aos controles de plataforma `PLT-xx`. Num repositório privado o Scorecard não publica e o selo não se
aplica; os controles PLT, que são o que de fato protege a entrega, continuam todos verificáveis.

O perfil `internal` em `conformance/levels.yaml` mantém **todos** os controles PLT de cada nível e os limiares de
cobertura, e dispensa só os dois selos públicos. `aedis platform validate --level N --profile internal` e
`aedis org audit --profile internal` aplicam a régua sem eles; como evidência da organização, o `org audit` lê o que
ele mesmo aplica por `--apply` (ruleset `main`, permissões do Actions, Dependabot e secret scanning). A regra de
hoje vale igual: um tenant pode subir limiares, nunca baixar. O perfil `public` é o atual e continua o padrão.

## Entregas

Uma linha por item do levantamento com o dono (oito do primeiro levantamento, quatro do segundo); cada linha é uma issue com motivação, escopo, critério de aceite e
o controle PLT afetado. Nenhuma está implementada; este documento é o plano.

| # | Entrega | Entrada / arquivo | Critério de aceite | PLT | Issue |
|---|---|---|---|---|---|
| 1 | Perfil `internal`: controles PLT sem Scorecard/Best Practices | `conformance/levels.yaml` (`profiles`), `aedis platform validate --profile internal`, `aedis org audit --profile internal` | `validate --level 2 --profile internal` passa num repo privado sem Scorecard e sem selo, e falha quando falta um controle PLT do nível; `org-audit-summary.json` sem itens de selo | nenhum dispensado | [#80](https://github.com/aedis-build/aedis-platform/issues/80) |
| 2 | GitHub Packages para NuGet e npm | `release-nuget.yml` e `release-npm.yml`: entrada `registry: github`; publicação com `GITHUB_TOKEN` (`packages: write`), sem `--provenance`; atestação de artefato verificada com `gh attestation verify` | pacote publicado no Packages da org só com o `GITHUB_TOKEN`; `gh attestation verify` passa para `.nupkg` e `.tgz`; `release-attach.yml` anexa o `.sigstore`; callers públicos inalterados | PLT-07 | [#81](https://github.com/aedis-build/aedis-platform/issues/81) |
| 3 | Release manual na `main`, tag sob demanda | `release-cut.yml`: entrada `tag-environment`; job de tag separado sob `environment` com reviewers; caller `workflow_dispatch` só na `main` | dispatch sem aprovação não cria tag e o output `tag` fica vazio; com aprovação cria `vX.Y.Z` no SHA aprovado; push em `main` não corta release; callers atuais inalterados | PLT-10, PLT-22 | [#82](https://github.com/aedis-build/aedis-platform/issues/82) |
| 4 | Monorepo: matriz por caminho e `working-directory` | reusable `changed-projects.yml` (entrada `projects`, output `matrix`); `working-directory` em `aedis-ci.yml`, `coverage.yml`, `coverage-js.yml`, `sast.yml`; artefatos `*-summary-<nome>`; `merge-gate.yml` lê sumários com sufixo | PR que altera só `src/a` roda só `a`; dois projetos no mesmo run publicam sumários sem colisão e o portão lê os dois; repo de um projeto não muda | PLT-13, PLT-21 | [#83](https://github.com/aedis-build/aedis-platform/issues/83) |
| 5 | Sonar como SAST, runner self-hosted só nesse job | `aedis-ci.yml`: entradas `sast: sonar` (pula o Semgrep) e `sast-runs-on` (só o job `sonar`); action `.github/actions/sast-sonar-summary` converte o quality gate em `sast-summary.json` (`aedis.platform/sast-summary/v1`); a varredura fica no job do caller com a action da empresa | com `sast: sonar` nenhum job Semgrep roda e `sast-summary` tem `result` igual ao quality gate; `merge-gate.yml` aprova com gate verde e bloqueia com vermelho sem mudar a política; `sast-runs-on` não afeta os outros jobs | PLT-13 | [#84](https://github.com/aedis-build/aedis-platform/issues/84) |
| 6 | DAST por tipo de app, sem ferramenta própria | `aedis-ci.yml` `dast-start`: API .NET e SSR por `docker compose -f compose.dast.yaml up -d --wait`; site estático por `pnpm build && pnpm dlx serve out -l 8080 &`; `compose.dast.yaml` de referência em `examples/` | as três receitas produzem `dast-summary` com `result: pass` sem passo além do `dast-start`; o compose de exemplo responde em `localhost:8080` no `ci.yml` da plataforma | PLT-14 | [#85](https://github.com/aedis-build/aedis-platform/issues/85) |
| 7 | Node SSR em Kubernetes | `charts/aedis-app`: `type: ssr` (comportamento de `site` + `PORT`/`NODE_ENV`); `template/ssr/Dockerfile` distroless `nodejs22` nonroot por digest; `aedis platform new-app --type ssr` | `helm template` com `type: ssr` passa no schema, kubeconform e Trivy config; `build-image.yml` constrói o Dockerfile com um SSR mínimo no `ci.yml` e a imagem responde em `:8080`; `type: site` não muda | PLT-01, PLT-06, PLT-07 | [#86](https://github.com/aedis-build/aedis-platform/issues/86) |
| 8 | Azure Container Apps com atestação verificada | reusable `deploy-aca.yml`: `app`, `resource-group`, `image`, `digest`, `environment`, `azure-*-id`; `azure/login` por OIDC federado; `gh attestation verify oci://…@digest` e `cosign verify` antes de `az containerapp update --image …@digest`; `deploy-summary.json` | digest sem atestação falha antes do update; com atestação a revisão referencia a imagem pelo digest e o sumário registra; o update roda sob o `environment` com reviewers | PLT-06, PLT-08, PLT-19 | [#87](https://github.com/aedis-build/aedis-platform/issues/87) |
| 9 | Imagens em GHCR ou Azure Container Registry | `build-image.yml`: entrada `registry` (`ghcr \| acr`), login no ACR por OIDC federado (`azure/login` + `az acr login`), mesma atestação pelo digest nos dois; `deploy-aca.yml` (#87) e a policy Kyverno (PLT-19) verificam contra o registro de origem | a mesma reusable publica a mesma build em GHCR ou ACR só mudando a entrada; `gh attestation verify` passa para a imagem no ACR; `deploy-aca` e Kyverno verificam sem configuração extra | PLT-06, PLT-07, PLT-19 | [#89](https://github.com/aedis-build/aedis-platform/issues/89) |
| 10 | Validação de Conventional Commits dentro do cálculo de versão | `scripts/next-version.sh` valida os commits desde a última tag antes de propor a versão; regra numa fonte só, compartilhada com `conventional-commits.yml` (que continua como check de PR) | `release-cut` falha em commit fora do padrão apontando SHA e mensagem; check de PR e `release-cut` concordam sempre; `--self-test` cobre o caso inválido | PLT-22 | [#90](https://github.com/aedis-build/aedis-platform/issues/90) |
| 11 | Canal `preview`/`rc` por regra | `release-cut.yml`: entrada `channel` (`stable \| rc \| preview`), regra no caller (`rc` por input de dispatch, `preview` fora da `main`); `X.Y.Z-rc.N` / `X.Y.Z-preview.N` com `N` a partir das tags existentes; tag e release como pré-release; a estável seguinte reaproveita o `X.Y.Z` do último rc | `next-version.sh --self-test` cobre `rc.1 → rc.2 → stable`; pré-release não move `v1`; PLT-22 continua: callers pinam só tag estável | PLT-22 | [#91](https://github.com/aedis-build/aedis-platform/issues/91) |
| 12 | Rebuild por ambiente em vez de promoção (modo suportado) | `build-image.yml`: entradas `environment` e `registry` por chamada, mesmo SHA de origem obrigatório, build reprodutível (base por digest, lockfile congelado, sem rede fora do pacote, `SOURCE_DATE_EPOCH`), uma atestação por build; artefato `build-summary.json` (`aedis.platform/build-summary/v1`) com SHA de origem e digest por ambiente | critério forte: dois builds do mesmo SHA dão o mesmo digest no `ci.yml` da plataforma; se a base não permitir, o `build-summary` registra os digests e a diferença; callers que chamam uma vez por versão não mudam | PLT-06, PLT-07, PLT-08, PLT-21 | [#92](https://github.com/aedis-build/aedis-platform/issues/92) |

**Ressalva sobre o rebuild (#92).** O padrão do Aedis continua ser promover o mesmo digest entre ambientes: o que
foi testado é o que roda (princípio SLSA, PLT-08). O rebuild por ambiente é modo suportado só sob a condição de
reprodutibilidade verificada; quando dois builds do mesmo SHA divergem, a divergência fica registrada no
`build-summary` em vez de passar despercebida.

## Fluxo de ambientes

Os ambientes são da empresa e a plataforma não impõe o seu. O mapeamento que o plano assume:

| Branch ou tag | Ambiente | Como chega lá |
|---|---|---|
| `develop` | dev | `aedis-ci.yml` no PR e no push; imagem por `build-image.yml` com versão de pré-release; `argocd-image-updater` escreve `image.tag` em `env/dev.yaml` |
| `main` | qas | o mesmo, em `env/qas.yaml` |
| tag `vX.Y.Z` | prod | `release-cut.yml` por `workflow_dispatch` na `main`, tag aprovada no environment `production` (#82); a tag chama `build-image.yml` e promove por PR em `env/prod.yaml` (`promote.yml`, PLT-08/PLT-10) ou pelo `deploy-aca.yml` (#87) |

## Ordem sugerida

1. **#80 perfil `internal`**: destrava `validate` e `org audit` no repositório privado; tudo o mais é medido por ele.
2. **#83 monorepo** e **#84 Sonar**: são o CI de toda PR; sem eles o `aedis-ci.yml` não roda na forma dos repositórios da empresa.
3. **#81 Packages**, **#82 release manual**, **#90 validador no release-cut** e **#91 canal rc/preview**: a esteira
   de release.
4. **#85 DAST**, **#86 SSR**, **#89 ACR**, **#87 Container Apps** e **#92 rebuild por ambiente**: a superfície
   exposta, a imagem e o deploy.

Cada issue sai numa release minor da plataforma (entrada nova com padrão igual ao comportamento atual, sem quebra
para os callers públicos); a empresa repina pelo Dependabot como qualquer caller.

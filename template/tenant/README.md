# gitops-apps

Declaração dos workloads de `__AEDIS_ORG__` entregues pelo [Aedis Platform](https://github.com/aedis-build/aedis-platform)
(chart `aedis-app` __AEDIS_CHART_VERSION__, nuvem `__AEDIS_CLOUD__`).

```
platform/values/<env>.yaml   # fatos do ambiente: nuvem, cofre, ingress, rede, telemetria
apps/<app>/app.yaml          # declaração do app
apps/<app>/env/<env>.yaml    # imagem e ajustes por ambiente
argocd/applicationset.yaml   # como o ArgoCD lê este repo
.github/workflows/           # validação (chart, schema, kubeconform, Trivy) a cada PR
```

## Fluxo

1. O repo da aplicação publica a imagem `X.Y.Z-rc.N` (assinada, com SBOM) pelo `release.yml` (workflow reutilizável `build-image.yml`).
2. O `argocd-image-updater` escreve `image.tag` em `apps/<app>/env/<não-prod>.yaml`; o ArgoCD faz o canário.
3. Com SAST/DAST verdes, o `promote.yml` abre a PR `promote/<app>-<versão>` em `env/prod.yaml`
   com `image.tag` **e** `image.digest`. Aprovação de `@__AEDIS_ORG__/platform-security`; sync manual em prod.
4. Rollback: `git revert` do commit do bot (não-prod) ou PR trocando a tag/digest (prod).

## Validar localmente

```bash
aedis platform validate            # ou: scripts do aedis-platform
aedis platform validate --level 2  # checa os controles do nível L2
```

## Pré-requisitos no cluster

Instalados por `charts/aedis-cluster` do aedis-platform: Argo Rollouts, External Secrets Operator com o
`ClusterSecretStore aedis-secrets`, KEDA, Prometheus Operator (ou o backend escolhido), ingress controller
do preset `__AEDIS_INGRESS_PRESET__`, Kyverno (nível L2+).

# Contribuindo

## Regras do repositório

- **Nada de fornecedor no default.** Tudo que é de uma nuvem ou de um produto entra como preset opcional
  (`ingress.preset`, `observability.vendor`, `rollout.analysis.provider`), desligado por padrão, em arquivo
  próprio e com teste próprio. Um preset nunca altera o baseline de segurança.
- **Zero referência a terceiros de origem** nos arquivos (o CI roda um grep de vazamento).
- **Actions pinadas por SHA**, `permissions: {}` por padrão, só OIDC — nunca credenciais de longa duração.
- **Conventional Commits obrigatórios** em todo commit e no título da PR (`feat(chart): ...`, `fix(cli): ...`, `docs: ...`,
  `feat!:` para breaking). O CI bloqueia o que foge do padrão (`.github/workflows/conventional-commits.yml`); instale o hook
  local com `scripts/install-git-hooks.sh`. É deles que o versionamento semântico (chart, CLI, workflows) é derivado.
- **Semver em tudo**: tags de imagem (`X.Y.Z`, `X.Y.Z-rc.N`; o schema do chart rejeita outros formatos), versão do chart
  (`Chart.yaml` = tag `chart-aedis-app-vX.Y.Z`), CLI e workflows (`v1` flutuante + `v1.x.y`).

## Chart

```bash
helm plugin install https://github.com/helm-unittest/helm-unittest.git
for f in charts/aedis-app/ci/*.yaml; do helm lint charts/aedis-app -f "$f"; done
helm unittest charts/aedis-app
scripts/e2e-kind.sh            # kind + Argo Rollouts + ESO fake + KEDA
```

Toda chave nova em `values.yaml` precisa: entrada em `values.schema.json`, teste em `tests/`, cobertura por um
`ci/*.yaml` quando muda o render, e uma linha em `docs/chart.md`.

## CLI

```bash
dotnet test cli/Aedis.Cli.slnx
dotnet run --project cli/Aedis.Cli -- platform validate --chart ./charts/aedis-app --repo /tmp/tenant
```

O template do tenant (`template/tenant`) e `conformance/levels.yaml` são embarcados na CLI; alterar um deles
exige rodar os testes da CLI.

## Níveis de conformidade

Mudanças em `conformance/levels.yaml` só sobem limiares em versões major do chart. Novos controles ganham um
`PLT-xx`, uma linha em `docs/conformance.md` e, quando verificáveis localmente, uma checagem em
`cli/Aedis.Cli/Validation/ConformanceChecker.cs`.

## Proteção da branch `main`

O ruleset vive em `.github/rulesets/main.json` (só PR com squash, histórico linear, checks `Conventional Commits`,
`Chart aedis-app` e `Template do tenant valida com o chart` obrigatórios e atualizados, sem force-push nem
deleção). Aplicar/atualizar:

```bash
gh api -X POST repos/aedis-build/aedis-platform/rulesets --input .github/rulesets/main.json
```

Com um segundo mantenedor, suba `required_approving_review_count` para 1 e ligue `require_code_owner_review`.

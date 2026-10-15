# Contribuindo

## Regras do repositório

- **Nada de fornecedor no default.** Tudo que é de uma nuvem ou de um produto entra como preset opcional
  (`ingress.preset`, `observability.vendor`, `rollout.analysis.provider`), desligado por padrão, em arquivo
  próprio e com teste próprio. Um preset nunca altera o baseline de segurança.
- **Zero referência a terceiros de origem** nos arquivos (o CI roda um grep de vazamento).
- **Actions pinadas por SHA**, `permissions: {}` por padrão, só OIDC — nunca credenciais de longa duração.
- Commits convencionais (`feat(chart): ...`, `fix(cli): ...`, `docs: ...`).

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

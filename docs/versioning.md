# Versões, suporte e publicação dos workflows

Os workflows reutilizáveis (`.github/workflows/*.yml` com `workflow_call`) deste repositório são publicados como
releases `vX.Y.Z`, e é só por elas que um repositório os consome. As actions do Aedis no GitHub Marketplace
([aedis-release-cut](https://github.com/aedis-build/aedis-release-cut), [aedis-dast](https://github.com/aedis-build/aedis-dast),
[aedis-conformance](https://github.com/aedis-build/aedis-conformance), [aedis-sca](https://github.com/aedis-build/aedis-sca))
vivem cada uma no seu repositório, com a mesma
política: `release-cut` a cada push em `main`, release imutável e atestada pelo `release-tag.yml` desta plataforma,
tag maior móvel, e o consumo só pelo SHA de uma tag `vX.Y.Z`. Esta plataforma as consome assim também
(`release-cut.yml`, `aedis-ci.yml`, `coverage.yml`, `coverage-js.yml`, `validate.yml`, `conventional-commits.yml`).

## Versão

Semver derivado dos Conventional Commits desde a release anterior, pelo `release.yml` a cada push em `main`
(`next-version.sh` da action aedis-release-cut):

| Commits desde a última tag | Incremento |
|---|---|
| `tipo!:` ou rodapé `BREAKING CHANGE` | major |
| `feat` | minor |
| `fix`, `perf`, `refactor`, `revert`, `build(deps)` | patch |
| só `docs`, `ci`, `chore`, `test`, `style` | nenhuma release |

Uma release é: a tag `vX.Y.Z`; a GitHub Release (`release-tag.yml`, reutilizável) com as notas geradas dos commits (impacto da atualização primeiro,
depois features, correções, dependências), **imutável** (a tag não se move e os assets não mudam depois de
publicada; configuração do repositório, conferida pelo workflow) e **atestada** pelo GitHub
(`gh release verify vX.Y.Z -R aedis-build/aedis-platform`); e a tag maior `v1`, movida para a última release do
major, para humanos e docs. [CHANGELOG.md](../CHANGELOG.md) é regenerado a partir das notas de todas as releases num
pull request `docs(changelog)` que o próprio `release.yml` abre e atualiza.

## Suporte

- **Um major suportado por vez**: o atual. O anterior recebe correções só até a primeira minor do novo.
- **Quebra de compatibilidade só em major**: remover ou renomear um input, um output, um secret ou um artefato,
  mudar um default que altera o resultado, exigir uma permissão a mais do caller.
- **Aviso uma minor antes**: o que vai ser removido no próximo major emite, no workflow, um
  `echo "::warning::deprecated: <o que> is removed in v<N+1>; use <o quê>"` desde a minor que introduz a
  substituição, e a nota da release o lista em *Deprecations*.
- **Patch para correções**, inclusive de segurança, sem mudança de interface; o Dependabot leva ao caller.
- **Fallback `v1` no checkout da plataforma**: quando o caller não passa `platform-ref`, os reusables buscam os
  arquivos da plataforma (regras Semgrep, scripts, CLI, chart) pela tag maior `v1`, a última release do major.
  Nenhum contexto do GitHub entrega ao reutilizável o commit em que ele foi chamado (numa chamada entre repositórios
  `job_workflow_sha` e `job_workflow_ref` chegam vazios), então o único override determinístico é o input
  `platform-ref` com o SHA da tag pinada; o CI desta plataforma confere que `v1` aponta para uma release. Enquanto
  não há consumidor externo, os repositórios da organização acompanham a última release, e as cópias de
  compatibilidade em `scripts/` são cortesia, não compromisso: saem na v2.

## Como um repositório consome

Pin pelo **SHA do commit da tag** com a tag no comentário, em workflows e actions; nunca um commit de branch, nunca
a tag solta (uma tag pode ser movida por quem tem escrita; o SHA não):

```yaml
    uses: aedis-build/aedis-platform/.github/workflows/aedis-ci.yml@<sha da tag> # v1.2.3
      - uses: aedis-build/aedis-dast@<sha da tag> # v1.0.1
      - uses: aedis-build/aedis-release-cut@<sha da tag> # v1.0.1
      - uses: aedis-build/aedis-conformance@<sha da tag> # v1.0.1
      - uses: aedis-build/aedis-sca@<sha da tag> # v1.0.1
```

Com o `package-ecosystem: github-actions` no `.github/dependabot.yml` (os templates do tenant e do site já o trazem),
o Dependabot abre o pull request de repin a cada release, SHA e comentário juntos. `aedis platform init` e
`new-site` já escrevem esse formato (`--platform-ref v1` resolve para a release mais alta do major), e
`aedis platform validate --level 2` falha (controle PLT-22) quando um `uses:` da plataforma ou de uma das quatro actions
não aponta para o SHA de uma tag `vX.Y.Z`.

Para quem dá manutenção: a primeira release de um major é cortada à mão (`gh workflow run release -f bump=major`);
as seguintes saem sozinhas a cada push em `main` que libera algo.

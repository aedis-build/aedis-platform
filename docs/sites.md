# Sites (Next.js + Aedis UI)

Um site é uma aplicação web servida a pessoas, construída com os componentes do [Aedis UI](https://github.com/aedis-build/aedis-ui).
O Aedis Platform o trata como trata uma API .NET: o repositório do site não carrega nenhuma cola própria. O CI,
as regras de SAST, o gate de cobertura, o DAST, a imagem e o chart vêm todos da plataforma.

## 1. Criar o repositório

```bash
aedis platform new-site portal --org minha-org
cd portal && pnpm install && pnpm dev
```

Gera um Next.js 16 (App Router, `output: "standalone"`, pnpm) com:

| Arquivo | Para quê |
|---|---|
| `components.json` | registry `@aedis` configurado: `pnpm dlx shadcn@latest add @aedis/button` adiciona componentes como código seu |
| `proxy.ts` + `lib/security-headers.ts` | cabeçalhos do `@aedis/security-headers` em toda resposta de página, com nonce por requisição (CSP sem inline nem eval, HSTS, sem framing) |
| `Dockerfile` | multi-stage: build no `node:22` e runtime no `gcr.io/distroless/nodejs22` *nonroot*, ambos pinados por digest; sem shell; só `.next/standalone` na imagem; porta 8080 |
| `pnpm-workspace.yaml` | `blockExoticSubdeps`, `minimumReleaseAge: 10080` (uma semana) e `trustPolicy: no-downgrade` |
| `vitest.config.ts` | reporter Cobertura e limiares 85 % linhas / 70 % branches (L3); testes com fast-check (conta para o *Fuzzing* do Scorecard) |
| `.github/workflows/aedis-ci.yml` | chama o `aedis-ci.yml` da plataforma com `language: node` e `dast-start` |
| `.github/workflows/release.yml` | em tag `vX.Y.Z`, chama o `build-image.yml` (imagem assinada, SBOM, proveniência) |
| `.github/dependabot.yml` | npm, actions e docker, semanal, cooldown de 7 dias |
| `dast-accept.yaml`, `SECURITY.md`, `README.md` | aceites do DAST com validade, política de vulnerabilidades, guia do repositório |
| `deploy/app.yaml` | values do chart `aedis-app` com `type: site` |

## 2. CI: `aedis-ci.yml` com `language: node`

```yaml
jobs:
  aedis:
    uses: aedis-build/aedis-platform/.github/workflows/aedis-ci.yml@<sha da tag> # vX.Y.Z
    permissions: { contents: read, actions: read, packages: read, security-events: write, id-token: write }
    with:
      language: node
      min-line: 85
      min-branch: 70
      no-regression: true
      dast-start: "pnpm build && pnpm start > site.log 2>&1 &"
```

O caller do template também limita o CI a uma execução por pull request. O `concurrency` fica no nível do
workflow, agrupado pelo número do PR, e o gatilho `pull_request` inclui `closed`. Assim, um push novo ou o merge
do PR cancelam a execução que estava rodando, e numa pilha de PRs as execuções obsoletas não ficam na fila. O job
tem `if: github.event.action != 'closed'`, então a execução do fechamento só cancela e termina. Push na `main` e o
agendamento usam `run_id` como grupo e nunca são cancelados: a comparação de cobertura com o último run verde
depende deles.

Com `language: node` o pipeline troca só o que é da linguagem: a cobertura roda pelo `coverage-js.yml`
(`pnpm install --frozen-lockfile`, o script `test:coverage` do repositório, o mesmo `scripts/coverage-gate.py` e o
mesmo `coverage-summary.json` do .NET), o CodeQL analisa `javascript-typescript` sem build e o SAST soma
`p/javascript p/typescript p/react` às regras Aedis para TypeScript/React. Conventional Commits, REUSE e o
Codecov opcional são iguais para as duas linguagens. A imagem assinada com SBOM e proveniência sai do `release.yml`,
separado porque precisa de permissões de escrita que o CI de toda PR não deve ter.

`dast-start` é opcional e vale para .NET também: é o comando que constrói e serve a app em `http://localhost:8080`.
Com ele, o job `dast` instala a toolchain da linguagem, roda o comando, espera a porta responder e chama a action
`.github/actions/dast` (ZAP baseline) contra `http://localhost:8080/`, com os aceites de `dast-accept.yaml`.
Um aceite pode se limitar a caminhos (`paths`), para um falso positivo numa URL não esconder o mesmo alerta no resto
do site:

```yaml
accept:
  - pluginId: 90003
    until: 2027-01-20
    reason: llms-full.txt é text/plain; o ZAP lê o código de exemplo como HTML
    paths: [/llms-full.txt]
```

## 3. Deploy: `type: site` no chart

```yaml
# apps/portal/app.yaml
name: portal
namespace: web
type: site
image:
  repository: ghcr.io/minha-org/portal
security:
  uid: 65532          # nonroot das imagens distroless
  gid: 65532
  writablePaths:
    - path: /app/.next/cache
ingress:
  enabled: true
  hosts: [portal.minha-org.com]
  edge:
    waf: true
```

Um site é servido como uma `api`: Service HTTP, Ingress (qualquer preset), piso de 2 réplicas, HPA, PDB,
NetworkPolicy, análise HTTP do canário e a declaração de WAF em `ingress.edge.waf`. O que muda: o chart não
injeta as variáveis do framework Aedis (`ASPNETCORE_*`, `Security__*`), as probes HTTP padrão apontam para `/`
(um caminho explícito diferente é respeitado, inclusive nos healthchecks do ALB, AGIC e Kong) e não há segredos
de banco ou fila. `aedis platform new-app portal --type site` gera o `app.yaml` acima sem bloco `secrets`.

## 3b. Site estático na Cloudflare: `deploy-static.yml`

Um site sem servidor (Next.js com `output: "export"`) pode ir para a Cloudflare em vez do cluster. Os cabeçalhos de
segurança saem de um `_headers` gerado no build (CSP com hashes dos scripts inline, já que não há nonce por
requisição) e servido pelo runtime da Cloudflare. O CI continua o mesmo `aedis-ci.yml`; o deploy é outro
reutilizável:

```yaml
# .github/workflows/deploy.yml
on:
  push:
    branches: [ main ]
  workflow_dispatch:
permissions: {}
jobs:
  deploy:
    uses: aedis-build/aedis-platform/.github/workflows/deploy-static.yml@<sha da tag> # vX.Y.Z
    permissions: { contents: read, id-token: write, attestations: write }
    with:
      name: meu-site
      url: https://meu-site.example
    secrets: inherit
```

O `secrets: inherit` é obrigatório: um workflow reutilizável só recebe os segredos que o caller repassa, mesmo
num job que declara o environment. As regras do `prd` (só `main`, aprovador obrigatório) continuam valendo para o
job que lê os segredos.

O workflow constrói o site uma vez, empacota, gera o SBOM (SPDX) e atesta o pacote. Como o build roda num workflow
reutilizável da plataforma, isolado do repositório, a atestação atende **SLSA Build L3**. O job de deploy declara o
environment (`prd` por padrão), verifica a atestação exigindo que ela tenha saído do `deploy-static.yml` e publica
exatamente aqueles bytes com o `wrangler` do lockfile do site.

| No repositório do site | Para quê |
|---|---|
| `wrangler.jsonc` | Worker de assets estáticos apontando para `out/`, rotas do domínio |
| environment `prd` | `CLOUDFLARE_API_TOKEN` (Workers Editor + rotas) e `CLOUDFLARE_ACCOUNT_ID`; só `main`, aprovador obrigatório, sem bypass de admin |
| `wrangler` em `devDependencies` | versão exata, atualizada pelo Dependabot com cooldown |

Para conferir o que está no ar:

```bash
gh attestation verify meu-site.tgz -R <org>/<repo> --signer-workflow aedis-build/aedis-platform/.github/workflows/deploy-static.yml
```

### DAST do site estático: `wrangler dev` no CI, não contra o site no ar

O DAST roda no `aedis-ci.yml`, com o `wrangler dev` do lockfile servindo o build. O runtime local aplica o mesmo
`_headers`, então o ZAP vê os cabeçalhos reais. O modo local não pede login nem segredo, e a rota `custom_domain`
do `wrangler.jsonc` não atrapalha:

```yaml
      dast-start: "pnpm build && (WRANGLER_SEND_METRICS=false pnpm exec wrangler dev --ip 127.0.0.1 --port 8080 > dast-server.log 2>&1 &)"
```

Não rode o DAST contra a URL publicada, depois do deploy. A partir dos runners do GitHub, a Cloudflare responde com
a página de desafio de bot, e o ZAP avalia a CSP dessa página, não a do site. Isso aconteceu no aedis-ui em
2026-10-22 ([#60](https://github.com/aedis-build/aedis-platform/issues/60)).

No `_headers`, cada caminho recebe no máximo uma CSP. Não use `/*` com uma CSP e `! Content-Security-Policy` nas
regras de página: em produção a Cloudflare não aplicou o detach na raiz `/` e enviou duas políticas, que o navegador
aplica juntas.

## 4. O que L3 significa para um site

| Exigência | Como o site a cumpre |
|---|---|
| DAST sem alerta alto não aceito | ZAP baseline contra o site servido em todo PR (`dast-start`); aceites com validade em `dast-accept.yaml` |
| WAF declarado em produção | `ingress.edge.waf: true` no `env/prod.yaml`; a conformidade (`waf-declared-prod`) recusa um site exposto sem a declaração |
| Cabeçalhos de segurança | emitidos pela própria app em `proxy.ts` (CSP com nonce, HSTS, nosniff, sem framing, sem vazamento de referrer, `Cache-Control: no-store` nas páginas); a borda pode reforçar, não substituir |
| Cobertura ≥ 85 % linhas / 70 % branches, sem regressão | `vitest --coverage` com reporter Cobertura e o gate do `coverage-js.yml` |
| Fuzzing | testes baseados em propriedades com fast-check |
| Imagem assinada, SBOM, proveniência, pin por digest | `release.yml` (`build-image.yml`) em tag `vX.Y.Z` + `image.digest` em prod, como para qualquer app |

A sessão fica em cookie `HttpOnly` emitido pelo backend; o site chama o backend só na mesma origem (`api()` do
Aedis UI). Por isso o site não tem segredos no cofre.

## 5. Release de um registry: `release-registry-js.yml`

Um repositório que publica um registry shadcn (JSON em `/r/<name>.json`, como o aedis-ui) faz a release por este
reutilizável, em tag `vX.Y.Z`. O build roda na plataforma, isolado do chamador, e a atestação sai em SLSA Build L3;
o job de publicação verifica a atestação antes de anexar à GitHub Release o pacote `<name>-<tag>.tgz`, o bundle
Sigstore `<name>-<tag>.tgz.sigstore` (o `@aedis/cli` o verifica antes de escrever qualquer arquivo) e o SBOM. A tag
precisa ser a `version` do `package.json` indicado em `version-file`.

```yaml
# .github/workflows/release.yml
on:
  push:
    tags: ["v[0-9]+.[0-9]+.[0-9]+*"]
permissions: {}
jobs:
  release:
    uses: aedis-build/aedis-platform/.github/workflows/release-registry-js.yml@<sha da tag> # vX.Y.Z
    permissions: { contents: write, id-token: write, attestations: write }
    with:
      name: meu-registry
      registry-dir: apps/www/public/r
```

Quem instala confere o que recebeu:

```bash
gh attestation verify meu-registry-v1.0.0.tgz -R minha-org/meu-registry   --signer-workflow aedis-build/aedis-platform/.github/workflows/release-registry-js.yml
```

## 6. Publicação de um pacote npm: `release-npm.yml`

Um pacote npm do Aedis (o `@aedis/cli`, por exemplo) é publicado por este reutilizável em tag `<prefixo>vX.Y.Z`:
a tag é conferida contra a `version` do `package.json`, os scripts de verificação e o build rodam no diretório do
pacote, o SBOM é gerado e `npm publish --provenance` publica por Trusted Publishing (OIDC). Nenhum `NPM_TOKEN` existe
em lugar nenhum; o npm mostra na página do pacote o repositório, o workflow e o commit que o publicaram.

```yaml
# .github/workflows/cli-release.yml
on:
  push:
    tags: ["cli-v[0-9]+.[0-9]+.[0-9]+"]
permissions: {}
jobs:
  publish:
    uses: aedis-build/aedis-platform/.github/workflows/release-npm.yml@<sha da tag> # vX.Y.Z
    permissions: { contents: read, id-token: write }
    with:
      package-dir: packages/cli
      tag-prefix: cli-
```

No npmjs.com, uma vez por pacote: em *Trusted Publisher*, registre o repositório chamador, o nome do arquivo do
workflow **chamador** (`cli-release.yml` no exemplo: o npm valida o workflow que iniciou a execução, não o
reutilizável) e o environment (`npm` por padrão); em *Publishing access*, exija 2FA e não permita tokens. Confirme na
primeira publicação que a página do pacote mostra a proveniência.

### Tarballs e atestações na GitHub Release: `release-attach.yml`

Depois dos jobs de `release-npm.yml` (um por pacote), um último job anexa à release da tag os tarballs exatamente
como o npm os recebeu, cada um com o bundle Sigstore (`.tgz.sigstore`) e o `integrity` nas notas, depois de verificar
cada atestação na API do GitHub e contra o bundle que vai para a release. Quem não alcança o npm instala e audita
offline: `gh attestation verify <tgz> --owner <org> && npm install ./<tgz>`.

```yaml
  release:
    needs: [publish, names]
    uses: aedis-build/aedis-platform/.github/workflows/release-attach.yml@<sha da tag> # vX.Y.Z
    permissions: { contents: write }
    with:
      title: "@aedis/cli ${{ github.ref_name }}"
```

## 7. Regressão visual: `visual-regression.yml`

Um site com baselines de screenshot (Playwright `toHaveScreenshot`) compara cada pull request com elas por este
reutilizável, na imagem oficial do Playwright pinada por digest, para que fontes e navegador sejam os mesmos em toda
execução. O modo `update` regenera as baselines e as sobe como artefato `visual-baselines`: o autor as commita e um
mantenedor olha o que mudou, porque uma baseline regenerada só prova que nada mudou desde que foi tirada.

```yaml
# .github/workflows/visual.yml
on:
  push:
    branches: [ main ]
  pull_request:
    types: [opened, synchronize, reopened, labeled]
permissions: {}
jobs:
  compare:
    if: github.event.action != 'labeled'
    uses: aedis-build/aedis-platform/.github/workflows/visual-regression.yml@<sha da tag> # vX.Y.Z
    permissions: { contents: read }
    with:
      working-directory: apps/www
  baselines:
    if: github.event.action == 'labeled' && github.event.label.name == 'update-visual-baselines'
    uses: aedis-build/aedis-platform/.github/workflows/visual-regression.yml@<sha da tag> # vX.Y.Z
    permissions: { contents: read }
    with:
      working-directory: apps/www
      mode: update
```

A versão da imagem (`playwright-image`) acompanha a do `@playwright/test` no lockfile: quando uma sobe, a outra sobe
no mesmo commit, e as baselines são regeneradas.


## 8. Corte de release: `release-cut.yml`

A próxima versão vem dos Conventional Commits desde a última tag (`scripts/next-version.sh`: breaking → major,
`feat` → minor, `fix`/`perf`/`refactor`/`revert`/`build(deps` → patch; o resto não libera). Num repositório cuja
versão vive em arquivos (`version-files`), cada push no branch padrão propõe um pull request `chore(release): vX.Y.Z`
que sobe a `version` deles, a dependência exata `pin-dependency` dentro deles, o `version-source` (um fonte com
`version-source-pattern`), os pins `<pin-pattern>X.Y.Z` dos docs e o lockfile (`lockfile-command`); quando o PR chega
ao branch padrão, o workflow põe uma tag por prefixo de `tag-prefixes` nesse commit, e os workflows de release em
`push: tags` assumem. Sem `version-files` (a versão só existe na tag) a tag vai direto no commit atual, sem PR. Nada é
proposto antes da primeira tag: a primeira release é cortada por uma execução manual com `bump` explícito.

Branch, PR e tags saem com o token de curta duração da GitHub App (secrets `app-id` e `app-private-key`, os
`AEDIS_APP_*` da organização): uma tag empurrada com o `GITHUB_TOKEN` não dispara workflow nenhum.

```yaml
# .github/workflows/release-cut.yml
on:
  push:
    branches: [ main ]
  workflow_dispatch:
    inputs:
      bump: { type: choice, options: [auto, patch, minor, major], default: auto }
permissions: {}
concurrency: { group: release-cut, cancel-in-progress: false }
jobs:
  cut:
    uses: aedis-build/aedis-platform/.github/workflows/release-cut.yml@<sha da tag> # vX.Y.Z
    permissions: { contents: read }
    with:
      bump: ${{ inputs.bump || 'auto' }}
      tag-prefixes: v cli-v
      version-files: package.json packages/cli/package.json packages/aedis-cli/package.json
      pin-dependency: "@aedis/cli"
      version-source: packages/cli/src/cli.ts
      version-source-pattern: 'export const VERSION = "{version}";'
      pin-pattern: "@aedis/cli@"
      lockfile-command: pnpm install --lockfile-only
    secrets:
      app-id: ${{ secrets.AEDIS_APP_ID }}
      app-private-key: ${{ secrets.AEDIS_APP_PRIVATE_KEY }}
```

Um repositório .NET, cuja versão é só a tag, chama o mesmo reutilizável sem `version-files`, só em
`workflow_dispatch`; a tag `vX.Y.Z` empurrada pela App dispara o `release-nuget.yml`.

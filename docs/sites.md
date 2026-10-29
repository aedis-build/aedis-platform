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

### Na Cloudflare: `--target cloudflare`

```bash
aedis platform new-site portal --org minha-org --target cloudflare --type static --domain portal.minha-org.com
aedis platform new-site portal --org minha-org --target cloudflare --type ssr --domain portal.minha-org.com
```

`--target kubernetes` (o padrão) gera o site acima. `--target cloudflare` gera o mesmo site para a borda da
Cloudflare (seção 3b), sem `Dockerfile`, `deploy/app.yaml` nem `release.yml`:

| Arquivo | Para quê |
|---|---|
| `wrangler.jsonc` | Worker com o domínio de `--domain` em `routes` (`custom_domain: true`), `workers_dev: false`, `preview_urls: false` e `compatibility_date` do dia (limitado à data do `workerd` do lockfile, que o `wrangler dev` exige); no `ssr`, `nodejs_compat` e `.open-next/worker.js` |
| `open-next.config.ts` | só no `ssr`: a configuração do OpenNext (`pnpm build:worker`) |
| `next.config.ts` + `scripts/headers.ts` | só no `static`: `output: "export"` e, depois do build, `out/_headers` com a CSP dos hashes de todo script inline (sem servidor, não há nonce nem `proxy.ts`) |
| `.github/workflows/deploy.yml` | em push na `main`, chama o `deploy-site.yml` pinado na tag da plataforma, com os segredos declarados um a um (nunca `secrets: inherit`); no `ssr`, rollback por `workflow_dispatch` |
| `.github/workflows/aedis-ci.yml` | o mesmo `aedis-ci.yml`, com o `dast-start` servindo o build pelo `wrangler dev` |
| `pnpm-workspace.yaml` | as regras de cadeia de suprimento do site, mais os `overrides` das advisories transitivas do wrangler e do OpenNext |

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
[Aedis DAST](https://github.com/aedis-build/aedis-dast) (ZAP baseline) contra `http://localhost:8080/`, com os aceites de `dast-accept.yaml`.
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

## 3b. Site na Cloudflare: `deploy-site.yml`

Um site pode ir para a borda da Cloudflare em vez do cluster. Um só reutilizável cobre os dois tipos: `type: static`
(export estático) e `type: ssr` (Next com servidor via [OpenNext](https://opennext.js.org/cloudflare), quando a app
precisa de CSP com nonce por requisição, route handlers ou Durable Objects). O CI continua o mesmo `aedis-ci.yml`;
o deploy é outro workflow. `provider: cloudflare` é o único por ora: a publicação é a action
[aedis-deploy-cloudflare](https://github.com/aedis-build/aedis-deploy-cloudflare), e um provedor novo é outra action
com os mesmos modos, sem mudar build, atestação, previews, rollout, rollback e smoke test, que ficam no reutilizável.

O workflow constrói o site uma vez, empacota, gera o SBOM (SPDX) e atesta o pacote. Como o build roda num workflow
reutilizável da plataforma, isolado do repositório, a atestação atende **SLSA Build L3**. O job de deploy declara o
environment, verifica a atestação exigindo que ela tenha saído do `deploy-site.yml` e publica exatamente aqueles
bytes com o `wrangler` do lockfile do site. Antes de publicar, a action confere o token (`/tokens/verify`: falha se
inválido, aviso se expira em menos de 14 dias), o `wrangler.jsonc` (`workers_dev: false`, `preview_urls: false`,
rota com `custom_domain: true`, `nodejs_compat` no SSR) e o tamanho do bundle.

### Site estático

```yaml
# .github/workflows/deploy.yml
on:
  push:
    branches: [ main ]
  workflow_dispatch:
permissions: {}
jobs:
  deploy:
    uses: aedis-build/aedis-platform/.github/workflows/deploy-site.yml@<sha da tag> # vX.Y.Z
    permissions: { contents: read, id-token: write, attestations: write, pull-requests: write, actions: read }
    with:
      type: static
      name: meu-site
      url: https://meu-site.example
    secrets:
      CLOUDFLARE_API_TOKEN: ${{ secrets.CLOUDFLARE_API_TOKEN }}
      CLOUDFLARE_ACCOUNT_ID: ${{ secrets.CLOUDFLARE_ACCOUNT_ID }}
```

Sem `secrets: inherit`: o reutilizável declara o que lê. Os dois segredos ficam no environment (`prd` por padrão:
só `main`, aprovador obrigatório, sem bypass de admin), nunca no repositório ou na organização. Como o job de deploy
declara o environment, o segredo do environment prevalece sobre o valor (vazio) que o caller repassa. As permissões
`pull-requests: write` e `actions: read` são dos jobs de preview e de rollback; o GitHub exige que o caller as
conceda mesmo quando esses jobs não rodam.

| No repositório do site | Para quê |
|---|---|
| `wrangler.jsonc` | Worker de assets estáticos apontando para `out/`, `workers_dev: false`, `preview_urls: false`, rota `custom_domain: true` |
| environment `prd` | `CLOUDFLARE_API_TOKEN` (escopos no README da action) e `CLOUDFLARE_ACCOUNT_ID`; só `main`, aprovador obrigatório |
| `wrangler` em `devDependencies` | versão exata, atualizada pelo Dependabot com cooldown |

### Site SSR (Next + OpenNext) num monorepo, com rollout e preview por PR

```yaml
# .github/workflows/deploy.yml
on:
  push:
    branches: [ main ]
  pull_request:
  workflow_dispatch:
    inputs:
      version-id:
        description: Rollback para esta versão do Worker (vazio = deploy normal)
        required: false
permissions: {}
jobs:
  deploy:
    uses: aedis-build/aedis-platform/.github/workflows/deploy-site.yml@<sha da tag> # vX.Y.Z
    permissions: { contents: read, id-token: write, attestations: write, pull-requests: write, actions: read }
    with:
      type: ssr
      name: meu-site
      working-directory: apps/web
      node-version: "24"
      environment: staging
      wrangler-env: staging
      url: https://staging.meu-site.example
      health-path: /api/health
      secret-names: SECURITY__HMAC_KEY
      percentage: 25
      preview: true
      rollback-version-id: ${{ inputs.version-id }}
    secrets:
      CLOUDFLARE_API_TOKEN: ${{ secrets.CLOUDFLARE_API_TOKEN }}
      CLOUDFLARE_ACCOUNT_ID: ${{ secrets.CLOUDFLARE_ACCOUNT_ID }}
      RUNTIME_SECRETS: ${{ secrets.RUNTIME_SECRETS }}
      SMOKE_HEADER: ${{ secrets.SMOKE_HEADER }}
```

`apps/web/wrangler.jsonc`, ao lado de `open-next.config.ts` (`export default defineCloudflareConfig()`):

```jsonc
{
  "name": "meu-site",
  "main": ".open-next/worker.js",
  "compatibility_date": "2026-10-01",
  "compatibility_flags": ["nodejs_compat"],
  "assets": { "directory": ".open-next/assets", "binding": "ASSETS" },
  "workers_dev": false,
  "preview_urls": false,
  "env": {
    "staging": { "routes": [{ "pattern": "staging.meu-site.example", "custom_domain": true }] },
    "prd": { "routes": [{ "pattern": "meu-site.example", "custom_domain": true }] },
    // Previews: outro Worker (meu-site-preview), Version URLs ligadas, sem rota e com os próprios bindings.
    "preview": { "preview_urls": true }
  }
}
```

| Comportamento | Como |
|---|---|
| Build | Linux (o OpenNext não constrói no Windows), `pnpm install` onde está o lockfile (a raiz do monorepo), `build-command` (padrão `opennextjs-cloudflare build`) em `working-directory`; o pacote leva `.open-next/` e a configuração do Wrangler |
| Segredos de runtime | `RUNTIME_SECRETS` do environment é um JSON nome → valor (`{"SECURITY__HMAC_KEY": "..."}`); só os nomes de `secret-names` vão ao Worker, por stdin, presos à versão; um nome declarado sem valor falha o job e diz qual |
| Rollout | `percentage` < 100: `wrangler versions deploy nova@P% anterior@(100-P)%`; o primeiro deploy de um Worker é sempre 100 % |
| Smoke test | depois do deploy: 200 em `/` e em `health-path`, CSP com nonce ou hashes, HSTS e `nosniff` (curl com `SMOKE_HEADER`), mais a action [aedis-dast](https://github.com/aedis-build/aedis-dast) com `edge: true` contra a `url`; com `percentage` < 100 e smoke falho, a versão anterior volta a 100 % |
| Desafio de bot | crie no WAF uma regra de skip para o cabeçalho de `SMOKE_HEADER` (`Nome: valor` aleatório, só no environment); nunca um bypass público |
| Preview por PR | `preview: true` em `pull_request`: versão com alias `pr-<n>` do Worker `env.preview`, no environment `preview` do GitHub (segredos próprios, nunca os de prd), URL comentada e atualizada na PR; nunca para PR de fork; `type: static` só constrói e verifica |
| Rollback | `workflow_dispatch` com `version-id`: lê o run que publicou a versão (a mensagem da versão traz o id do run), baixa o pacote daquele run, verifica a atestação e só então manda 100 % do tráfego para ela (dentro da retenção de artefatos) |
| Sem credenciais | o job de deploy verifica a atestação, avisa e não publica (dry-run); `environment: ""` roda sem environment (o CI da plataforma usa assim) |

Mudanças de classe de Durable Object (nova, renomeada, apagada) não saem como versão: a Cloudflare as aplica de uma
vez. Saem num deploy só delas, a 100 %.

Para conferir o que está no ar:

```bash
gh attestation verify meu-site.tgz -R <org>/<repo> --signer-workflow aedis-build/aedis-platform/.github/workflows/deploy-site.yml
```

Atestações de artefato num repositório **privado** exigem GitHub Enterprise Cloud; nos planos Free e Team, só em
repositórios públicos. Sem elas o build falha no `attest-build-provenance`, por desenho: o deploy não publica bytes
sem proveniência.

### DAST do site: `wrangler dev` no CI, não contra o site no ar

O DAST roda no `aedis-ci.yml`, com o `wrangler dev` do lockfile servindo o build. O runtime local aplica o mesmo
`_headers`, então o ZAP vê os cabeçalhos reais. O modo local não pede login nem segredo, e a rota `custom_domain`
do `wrangler.jsonc` não atrapalha:

```yaml
      dast-start: "pnpm build && (WRANGLER_SEND_METRICS=false pnpm exec wrangler dev --ip 127.0.0.1 --port 8080 > dast-server.log 2>&1 &)"
```

Não rode o DAST contra a URL publicada, depois do deploy. A partir dos runners do GitHub, a Cloudflare responde com
a página de desafio de bot, e o ZAP avalia a CSP dessa página, não a do site. Isso aconteceu no aedis-ui em
2026-10-22 ([#60](https://github.com/aedis-build/aedis-platform/issues/60)). O smoke test do `deploy-site.yml` é
outra coisa: confere a versão que acabou de entrar no ar e manda o cabeçalho de `SMOKE_HEADER` (regra de skip do WAF)
no curl e no ZAP. O check de borda da action aedis-dast ainda não envia cabeçalhos próprios; com o desafio de bot
ligado para os runners, ele avalia a página do desafio.

No `_headers`, cada caminho recebe no máximo uma CSP. Não use `/*` com uma CSP e `! Content-Security-Policy` nas
regras de página: em produção a Cloudflare não aplicou o detach na raiz `/` e enviou duas políticas, que o navegador
aplica juntas.

### Infra da Cloudflare como código

A zona onde o site é servido tem a sua postura de segurança no módulo Terraform
[`infra/cloudflare`](../infra/cloudflare/README.md) (provider `cloudflare/cloudflare` v5): TLS mínimo 1.2, Always Use
HTTPS e HSTS; Bot Fight Mode desligado e IA liberada (os crawlers de IA podem ler os sites); uma regra customizada que
só aceita `GET`, `HEAD` e `POST` nos hostnames, mais as suas até o limite do plano (5 no Free); a única regra de rate
limit do Free no `POST` das rotas de formulário (10 s, Block), que no Pro vira Managed Challenge em 1 minuto; no Pro,
o Cloudflare Managed Ruleset e o OWASP Core; Turnstile, Email Routing e buckets R2 quando pedidos.

```hcl
# infra/cloudflare/main.tf no repositório do site
module "zone" {
  source = "git::https://github.com/aedis-build/aedis-platform.git//infra/cloudflare?ref=<sha da tag>" # vX.Y.Z

  zone_name  = "meu-site.example"
  plan       = "free"
  hostnames  = ["meu-site.example"]
  form_paths = ["/api/contato"] # sem formulário: [] explícito
  turnstile  = {}
}
```

| Fica fora do módulo | Com quem |
|---|---|
| A zona | já existe; o módulo só a lê |
| Domínio próprio do Worker | `routes` com `custom_domain: true` no `wrangler.jsonc` (o wrangler cria DNS e certificado) |
| Segredos do Worker, inclusive o do Turnstile | `RUNTIME_SECRETS` do environment, presos à versão pelo `deploy-site.yml` |

Quem aplica é o dono da zona, com um token só para isso (escopos, `terraform import` do que já existe no painel e
backend opcional em R2 no [README do módulo](../infra/cloudflare/README.md)). O CI da plataforma roda
`terraform fmt -check`, `validate` e `test` (provider simulado) no módulo e nos exemplos, sem credenciais.

O `aedis platform validate --level 3` cobra o módulo de um site na Cloudflare (PLT-20, `waf-declared-prod`): com um
`wrangler.jsonc` de produção (rota `custom_domain`) na raiz ou em `apps/*/`, exige `workers_dev: false` e
`preview_urls: false` e um `infra/cloudflare/*.tf` com o módulo cujo `form_paths` cobre toda rota de formulário do
site (route handler do Next com `POST`), ou `form_paths = []` quando não há nenhuma.

## 4. O que L3 significa para um site

| Exigência | Como o site a cumpre |
|---|---|
| DAST sem alerta alto não aceito | ZAP baseline contra o site servido em todo PR (`dast-start`); aceites com validade em `dast-accept.yaml` |
| WAF declarado em produção | no cluster, `ingress.edge.waf: true` no `env/prod.yaml`; na Cloudflare, o módulo `infra/cloudflare` com o rate limit nas rotas de formulário; a conformidade (`waf-declared-prod`) recusa um site exposto sem a declaração |
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

A próxima versão vem dos Conventional Commits desde a última tag (action [Aedis Release Cut](https://github.com/aedis-build/aedis-release-cut): breaking → major,
`feat` → minor, `fix`/`perf`/`refactor`/`revert`/`build(deps` → patch; o resto não libera). Num repositório cuja
versão vive em arquivos (`version-files`), cada push no branch padrão propõe um pull request `chore(release): vX.Y.Z`
que sobe a `version` deles, a dependência exata `pin-dependency` dentro deles, o `version-source` (um fonte com
`version-source-pattern`), os pins `<pin-pattern>X.Y.Z` dos docs e o lockfile (`lockfile-command`); quando o PR chega
ao branch padrão, o workflow põe uma tag por prefixo de `tag-prefixes` nesse commit, e os workflows de release em
`push: tags` assumem. Sem `version-files` (a versão só existe na tag) a tag vai direto no commit atual, sem PR. Nada é
proposto antes da primeira tag: a primeira release é cortada por uma execução manual com `bump` explícito.

Branch, PR e tags saem com o token de curta duração da GitHub App (secrets `app-id` e `app-private-key`, os
`AEDIS_CODER_APP_*` da organização: a App de código, autora confiável; a App do portão só revisa): uma tag empurrada com o `GITHUB_TOKEN` não dispara workflow nenhum.

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
      app-id: ${{ secrets.AEDIS_CODER_APP_ID }}
      app-private-key: ${{ secrets.AEDIS_CODER_APP_PRIVATE_KEY }}
```

Um repositório .NET, cuja versão é só a tag, chama o mesmo reutilizável sem `version-files`, só em
`workflow_dispatch`; a tag `vX.Y.Z` empurrada pela App dispara o `release-nuget.yml`.

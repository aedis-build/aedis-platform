# infra/cloudflare

Módulo Terraform (provider `cloudflare/cloudflare` v5) com a postura de segurança de uma zona da Cloudflare que serve
sites e APIs do Aedis. Um módulo por zona. Cada site, API ou CDN da zona entra em `workloads` como uma declaração de
intenção: a mesma estrutura que o módulo de outro provedor recebe, sem módulo seletor entre provedores. O módulo não cria
Worker nem domínio de Worker: o wrangler e o `deploy-site.yml` seguem donos do script, das versões, do custom domain e
dos segredos.

## Workloads

```hcl
  workloads = {
    site = { type = "site-ssr", hostnames = ["example.com", "www.example.com"], forms = ["/api/contato"], turnstile = true }
    api  = { type = "api", hostnames = ["api.example.com"], headers = { "X-Origin-Verify" = var.origin_verify } }
    cdn  = { type = "cdn", hostnames = ["cdn.example.com"], bucket = "example-cdn", allowed_referers = ["example.com"] }
  }
```

| Campo | O que declara | O que o módulo gera |
|---|---|---|
| `type` | `site-static`, `site-ssr`, `api` ou `cdn` | os métodos aceitos: `GET`/`HEAD` (site-static, cdn), mais `POST` (site-ssr), mais `PUT`/`PATCH`/`DELETE`/`OPTIONS` (api); uma regra só para a zona inteira |
| `hostnames` | os hostnames do workload; cada um pertence a um workload só e está na zona | o escopo de toda regra, cabeçalho e widget do workload |
| `forms` | rotas que recebem POST (só site-ssr e api) | entram na regra de rate limit dos formulários, com os hostnames do workload |
| `turnstile` | `true` para ter um widget (não vale para cdn) | um widget `managed` nos hostnames; sitekey e segredo nos outputs `turnstile_sitekeys` e `turnstile_secrets`, por workload |
| `headers` | cabeçalhos de resposta | uma regra no ruleset único de cabeçalhos de resposta, nos hostnames do workload; na cdn, sobrepõem ou somam aos de segurança padrão |
| `rules` | regras de WAF só deste workload | cada expressão vira `(hostnames do workload) and (expressão)` no ruleset de regras customizadas da zona |
| `bucket`, `allowed_referers`, `manage_domain` | só cdn: o bucket R2, os domínios cujo Referer passa no hotlink e se o domínio próprio do R2 é do Terraform | bucket com dev URL desligado, domínio próprio do R2 por hostname (TLS 1.2) e a regra de hotlink |

O que é da zona continua na zona: `plan`, `bot_fight_mode`, `ai_bots`, `email_routing`, `custom_rules` (regras que
valem para a zona inteira), `r2_buckets` e os limiares e a ação do rate limit.

### Limites compartilhados da zona

Regras customizadas e de rate limit são da **zona**, não do site: todos os workloads dividem o mesmo teto.

| | Free | Pro |
|---|---|---|
| Regras customizadas | 5 | 20 |
| Regras de rate limit | 1, janela de 10 s | 2, janela até 1 minuto |

O módulo soma o que todos pedem (a regra de métodos, 1 de hotlink por cdn, as `rules` de cada workload e as
`custom_rules` da zona) e falha no plan quando passa do teto, dizendo quem consome o quê:

```
O plano free aceita 5 regras customizadas na zona e a configuração pede 6:
custom_rules (zona) 1, métodos (módulo) 1, workload cdn 1, workload site 3.
```

O rate limit nunca estoura: os `forms` de todos os workloads entram numa regra só, com os hosts e as rotas de cada um.
No Free ela é a única regra; sem forms em nenhum workload, a única regra é o limite geral. No Pro existem as duas. Por
isso os limiares (`form_requests_per_period`, `site_requests_per_period`) e a ação (`rate_limit_action`) são da zona: no
Free, todos os workloads cabem na mesma regra e não teriam como ter números diferentes.

| Recurso | Free | Pro |
|---|---|---|
| TLS mínimo 1.2, Always Use HTTPS, HSTS (2 anos, subdomínios, preload, nosniff) | sim | sim |
| Bot Fight Mode (`bot_fight_mode`, padrão ligado) | sim | substituído pelo Super Bot Fight Mode: bots verificados passam, automatizado sem identidade recebe Managed Challenge |
| Política de IA por comportamento (`ai_bots`): busca e agente liberados, treino bloqueado; AI Labyrinth opcional; robots.txt do site (não o gerenciado) | sim | sim |
| Rate limit por IP, ação `rate_limit_action` (padrão Managed Challenge com `mitigation_timeout = 0`, ou Block) | 1 regra: `POST` dos `forms` de todos os workloads (3 por 10 s) ou, sem forms, limite geral nos sites e APIs (100 por 10 s, fora cdn e bots verificados) | 2 regras: formulários (3 por minuto) e limite geral |
| Rulesets gerenciados | o Free Managed Ruleset roda sozinho | Cloudflare Managed + OWASP Core |
| Email Routing (destinos verificados, regras para Worker ou encaminhamento) | quando pedido | quando pedido |
| R2 (`r2_buckets` e os buckets das cdn), com o dev URL `r2.dev` desligado de forma explícita | quando pedidos | quando pedidos |
| Cabeçalhos de resposta: um ruleset só na fase `http_response_headers_transform` | quando um workload é cdn ou tem `headers` | idem |

### Bots e IA

Bot Fight Mode e política de IA são controles separados: liberar crawlers de IA no robots.txt (ou na política
`ai_bots`) não exige desligar o Bot Fight Mode. A política padrão libera busca (`search`) e agentes (`agent`) e bloqueia
treino (`training`, que inclui os crawlers mistos de busca e treino); cada uma aceita `allow`, `block` ou
`only_on_ad_pages`. `ai_bots.labyrinth` liga o AI Labyrinth, que desvia para páginas geradas os crawlers que ignoram o
robots.txt.

O Bot Fight Mode do Free tem custo:

- Não aceita exceção: nenhuma regra de skip o contorna. Um monitor de disponibilidade ou o smoke test do deploy podem
  levar desafio; prefira monitores que a Cloudflare reconhece como bot verificado.
- Ele injeta nas páginas o script de JavaScript Detections. Num site estático com CSP de hashes, o script não tem hash
  e é bloqueado: aparece como erro de CSP no console, sem afetar o site
  ([aedis-build/aedis.build#26](https://github.com/aedis-build/aedis.build/issues/26),
  [aedis-build/aedis-ui#239](https://github.com/aedis-build/aedis-ui/issues/239)). Num site SSR com nonce, a Cloudflare
  põe o nonce da resposta no script e ele roda.

Quem não aceita esse custo usa `bot_fight_mode = false`. No Pro o módulo desliga o Bot Fight Mode e liga o Super Bot
Fight Mode.

### Rate limit

A ação padrão é Managed Challenge: quem é gente passa pelo desafio e o contador zera. A API exige
`mitigation_timeout = 0` com desafio no Free, Pro e Business; com `rate_limit_action = "block"` a ação dura um período
(10 s no Free). `form_requests_per_period = 3` aceita três envios por IP na janela (10 s no Free, 1 minuto no Pro).

O limite geral (`site_requests_per_period = 100` a cada 10 s, 10 req/s sustentados) cobre todas as rotas e métodos nos
hostnames dos sites e APIs, fora as cdn (o camo do GitHub busca as imagens dos READMEs de poucos IPs) e os bots
verificados (os crawlers de busca liberados). A primeira carga de uma página de um site Next faz
algumas dezenas de requisições (HTML, chunks, CSS, fontes, imagens e o prefetch dos links), e vários visitantes atrás do
mesmo NAT dividem o IP; 100 deixa folga para isso e ainda pega um scraper ou uma inundação vinda de um só IP. No Free
ele só existe quando nenhum workload tem `forms`, porque o plano tem uma regra só.

### CDN (`type = "cdn"`)

A CDN é um workload como os outros: um bucket R2 servido por domínio próprio, sem Worker. Ficar em `workloads` deixa a
regra de métodos dela no mesmo termo de `GET`/`HEAD` dos sites estáticos (sem gastar uma regra do Free) e põe a
contagem dela na mesma soma dos limites.

| O quê | Como |
|---|---|
| Bucket | sem duplicar se ele também estiver em `r2_buckets`; dev URL desligado |
| Domínio próprio | um `cloudflare_r2_custom_domain` por hostname, TLS 1.2 (`manage_domain = false` deixa o domínio fora; veja abaixo) |
| Métodos | `GET` e `HEAD`; o resto leva block |
| Hotlink | Referer vazio passa (o camo do GitHub carrega as imagens dos READMEs sem Referer); Referer `https://<domínio>/…` ou `https://<sub>.<domínio>/…` de `allowed_referers` passa; o resto leva block |
| Cabeçalhos | `Content-Security-Policy: default-src 'none'; style-src 'unsafe-inline'; sandbox`, `Cross-Origin-Resource-Policy: same-site`, `Referrer-Policy: no-referrer`, `X-Content-Type-Options: nosniff`, mais os de `headers` |
| Cache | fora do módulo: `Cache-Control` vem do metadado de cada objeto no upload |

O hotlink usa o operador `wildcard`, que existe em todo plano (o `matches`, de regex, é só Business e Enterprise).
O wildcard casa a URL inteira, e o padrão exige `/` logo depois do host, então `evilaedis.build` e
`aedis.build.evil.com` não passam. O teste do módulo emula esse casamento sobre os padrões gerados. Hotlink é proteção
de banda, não de acesso: quem quer embutir sem permissão manda a página sem Referer (`referrerpolicy="no-referrer"`),
que passa de propósito.

### Cabeçalhos de resposta

A fase `http_response_headers_transform` tem **um ruleset só por zona**, e este módulo passa a ser o dono dele assim que
um workload é cdn ou tem `headers`: uma Transform Rule de cabeçalhos de resposta criada no painel some no apply. Todo
cabeçalho de resposta da zona entra pelo `headers` do workload dono do hostname (sempre `set`).

O guard do rlfn.studio exige o `X-Origin-Verify` na **requisição** que chega ao Worker; um cabeçalho de resposta não o
satisfaz. Para isso a borda precisa acrescentá-lo na fase `http_request_late_transform`, que este módulo ainda não
gerencia.

## Uso

```hcl
module "zone" {
  source = "git::https://github.com/aedis-build/aedis-platform.git//infra/cloudflare?ref=<sha da tag>" # vX.Y.Z

  zone_name = "rlfn.studio"
  plan      = "free"
  workloads = {
    site = { type = "site-ssr", hostnames = ["rlfn.studio", "staging.rlfn.studio"], forms = ["/api/proposals"], turnstile = true }
  }
  email_routing = { addresses = ["propostas@example.com"] }
}
```

Referência de todas as opções: [`examples/complete`](examples/complete/main.tf), uma zona fictícia (`example.com`,
plano Pro) com vários workloads (dois sites, uma API e uma cdn), todas as variáveis do módulo e, em cada bloco, o que
muda no Free; o `terraform test` planeja esse exemplo sem credenciais. Zonas reais:
[`examples/aedis-build`](examples/aedis-build/main.tf) (dois sites estáticos e a cdn) e
[`examples/rlfn-studio`](examples/rlfn-studio/main.tf) (SSR com formulário, Turnstile e Email Routing).

No repositório de um site, o arquivo fica em `infra/cloudflare/main.tf`, e o `aedis platform validate --level 3`
(controle PLT-20, `waf-declared-prod`) o confere contra cada `wrangler.jsonc` de produção: todo custom domain pertence
aos `hostnames` de algum workload, `workers_dev` e `preview_urls` estão desligados e toda rota POST do código (route
handler do Next) está no `forms` desse workload. As mensagens dizem o arquivo, o env e o hostname.

## Fora do módulo

| O quê | Quem cuida | Por quê |
|---|---|---|
| A zona | o dono, no painel | já existe; o módulo só a lê (`data "cloudflare_zone"`), então um `destroy` nunca a apaga |
| Domínio próprio do Worker | `routes` com `custom_domain: true` no `wrangler.jsonc` | o wrangler cria o registro DNS e o certificado a cada deploy; dois donos brigam |
| Segredos do Worker | `RUNTIME_SECRETS` do environment, enviados com a versão pelo `deploy-site.yml` | o segredo fica preso à versão (rollback leva o segredo certo); o segredo do Turnstile sai do output `turnstile_secrets` direto para lá |
| Transform Rules de requisição e de URL, Cache Rules, DNS de outros serviços | painel ou outro módulo | fora da postura de segurança (os cabeçalhos de resposta são do módulo: `headers` dos workloads) |

## Token

Um token de API só para este módulo (*My Profile → API Tokens → Create Custom Token*), restrito às zonas e à conta
em uso, com validade curta e só o que os recursos pedidos usam:

| Escopo | Permissão | Para quê |
|---|---|---|
| Zone | Zone: Read | o `data "cloudflare_zone"` |
| Zone | Zone Settings: Edit | TLS, HTTPS, HSTS |
| Zone | Zone WAF: Edit | regras customizadas, rate limit, rulesets gerenciados |
| Zone | Transform Rules: Edit | com um workload cdn ou com `headers` (fase de cabeçalhos de resposta) |
| Zone | Bot Management: Edit | Bot Fight Mode, IA, Super Bot Fight Mode |
| Zone | Email Routing Rules: Edit | só com `email_routing` |
| Account | Email Routing Addresses: Edit | só com `email_routing` |
| Account | Turnstile: Edit | com um workload com `turnstile = true` |
| Account | Workers R2 Storage: Edit | com `r2_buckets` ou um workload cdn (bucket, dev URL e domínio próprio) |

Este token não é o `CLOUDFLARE_API_TOKEN` do deploy: o do deploy publica Workers e não mexe no WAF.

## Aplicar numa zona que já existe

Configurações da zona e Bot Management são singletons: o primeiro apply só os ajusta. Já um ruleset de fase, um
widget, um destino ou regra de e-mail ou um bucket criados antes no painel precisam entrar no estado com
`terraform import`, senão o apply falha ao tentar criá-los de novo:

```bash
export CLOUDFLARE_API_TOKEN=...        # o token acima
ZONE_ID=...; ACCOUNT_ID=...            # Overview da zona, coluna da direita
api() { curl -s -H "Authorization: Bearer $CLOUDFLARE_API_TOKEN" "https://api.cloudflare.com/client/v4$1"; }
api "/zones/$ZONE_ID/rulesets" | jq -r '.result[] | select(.kind == "zone") | "\(.phase) \(.id)"'
api "/accounts/$ACCOUNT_ID/challenges/widgets" | jq -r '.result[] | "\(.name) \(.sitekey)"'
api "/accounts/$ACCOUNT_ID/email/routing/addresses" | jq -r '.result[] | "\(.email) \(.id)"'
api "/zones/$ZONE_ID/email/routing/rules" | jq -r '.result[] | "\(.matchers[0].value // "catch-all") \(.id)"'

terraform init
terraform import 'module.zone.cloudflare_ruleset.custom[0]' "zones/$ZONE_ID/<id da fase http_request_firewall_custom>"
terraform import 'module.zone.cloudflare_ruleset.rate_limit[0]' "zones/$ZONE_ID/<id da fase http_ratelimit>"
terraform import 'module.zone.cloudflare_ruleset.managed[0]' "zones/$ZONE_ID/<id da fase http_request_firewall_managed>"   # só no pro
terraform import 'module.zone.cloudflare_turnstile_widget.this["<workload>"]' "$ACCOUNT_ID/<sitekey>"
terraform import 'module.zone.cloudflare_email_routing_settings.this[0]' "$ZONE_ID"
terraform import 'module.zone.cloudflare_email_routing_address.this["<email>"]' "$ACCOUNT_ID/<id>"
terraform import 'module.zone.cloudflare_email_routing_rule.this["<para>"]' "$ZONE_ID/<id>"
terraform import 'module.zone.cloudflare_ruleset.response_headers[0]' "zones/$ZONE_ID/<id da fase http_response_headers_transform>"   # com cdn ou headers
terraform import 'module.zone.cloudflare_r2_bucket.this["<nome>"]' "$ACCOUNT_ID/<nome>/default"
# cloudflare_r2_managed_domain (o dev URL do bucket) não aceita import no provider 5.28.1 e não precisa: o create é um
# PUT que só desliga o r2.dev do bucket que já existe.
# cloudflare_r2_custom_domain também não aceita import, e o create de um domínio que já existe falha: veja abaixo.
terraform plan                          # leia: as regras importadas serão substituídas pelas do módulo
terraform apply
```

Importe só o que a listagem mostrou. O plan depois do import mostra a diferença entre o que estava no painel e o
módulo: uma regra feita à mão que precisa ficar vira item de `custom_rules` (zona) ou de `rules` do workload, e um
cabeçalho de resposta vira `headers` do workload.

**Antes do primeiro apply numa zona que já existe**, liste as regras reais de cada fase que o módulo vai assumir e
traga para variáveis tudo que o módulo não gera. Um levantamento pelo comportamento de fora (curl) não mostra tudo,
por exemplo uma Cache Rule ou uma regra desligada:

```bash
for phase in http_request_firewall_custom http_ratelimit http_response_headers_transform; do
  echo "== $phase"
  api "/zones/$ZONE_ID/rulesets/phases/$phase/entrypoint" | jq -r '.result.rules[]? | "\(.enabled) \(.action) \(.description)\n  \(.expression)"'
done
api "/accounts/$ACCOUNT_ID/r2/buckets/<bucket>/domains/custom" | jq '.result.domains'
```

### Domínio próprio de uma CDN que já existe

O provider 5.28.1 não importa `cloudflare_r2_custom_domain`, e criar um domínio que já está ligado ao bucket falha.
Enquanto o domínio for do painel, declare o workload cdn com `manage_domain = false`: o módulo cuida do resto (bucket, dev URL,
regras e cabeçalhos) e deixa o domínio como está. Para o Terraform assumir o domínio, numa janela combinada:

1. troque para `manage_domain = true` e rode o `terraform plan` (deve criar só `cloudflare_r2_custom_domain.cdn["<hostname>"]`);
2. remova o domínio do bucket no painel (R2 → bucket → Settings → Custom Domains) e rode o `terraform apply` logo em
   seguida. Até o certificado sair, a CDN responde erro; imagens embutidas em READMEs ficam quebradas por alguns minutos.

## Estado

Sem backend, o estado fica local (`terraform.tfstate`, ignorado pelo git). O estado guarda o segredo do Turnstile;
para dividir o estado entre pessoas ou rodar no CI, use um bucket R2 privado como backend S3 (criado à mão, fora do
módulo, com um token R2 de leitura e escrita só nele):

```hcl
terraform {
  backend "s3" {
    bucket                      = "aedis-tfstate"
    key                         = "cloudflare/rlfn.studio.tfstate"
    region                      = "auto"
    endpoints                   = { s3 = "https://<ACCOUNT_ID>.r2.cloudflarestorage.com" }
    use_path_style              = true
    use_lockfile                = true
    skip_credentials_validation = true
    skip_region_validation      = true
    skip_requesting_account_id  = true
    skip_metadata_api_check     = true
    skip_s3_checksum            = true
  }
}
```

As credenciais do backend vão em `AWS_ACCESS_KEY_ID` e `AWS_SECRET_ACCESS_KEY` (o par S3 do token R2).

## Quando a zona virar Pro

Troque `plan = "pro"` e rode o plan. Entram o Cloudflare Managed Ruleset e o OWASP Core (o OWASP pontua e bloqueia
acima do limiar padrão: acompanhe os Security Events na primeira semana e ajuste com `overrides` se houver falso
positivo), o rate limit passa a ter as duas regras (formulários em 1 minuto e o limite geral), cabem até 20 regras
customizadas e o Super Bot Fight Mode substitui o Bot Fight Mode: deixa passar os bots verificados e desafia o
automatizado sem identidade. Se a fase gerenciada já tiver um ruleset criado no painel, importe-o antes
(`cloudflare_ruleset.managed[0]`).

## CI

`terraform fmt -check`, `terraform validate` no módulo e nos exemplos e `terraform test` (provider simulado, sem
credenciais nem rede) rodam no `ci.yml` da plataforma. Nenhum job da plataforma aplica este módulo: quem aplica é o
dono da zona.

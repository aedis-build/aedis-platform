# Referência de todas as variáveis do módulo, numa zona fictícia (example.com) no plano Pro com vários workloads. Cada
# bloco diz o que muda no Free. Nada aqui é de uma zona real: troque nomes, hostnames e endereços antes de usar.
terraform {
  required_version = ">= 1.6"

  required_providers {
    cloudflare = {
      source  = "cloudflare/cloudflare"
      version = "5.28.1"
    }
  }
}

# Token em CLOUDFLARE_API_TOKEN (escopos no README do módulo); nunca no código.
provider "cloudflare" {}

variable "origin_verify" {
  description = "Valor do cabeçalho de verificação da API; fica no terraform.tfvars ou em TF_VAR_origin_verify, fora do git."
  type        = string
  sensitive   = true
}

module "zone" {
  # Num repositório de site: "git::https://github.com/aedis-build/aedis-platform.git//infra/cloudflare?ref=<sha da tag>"
  source = "../.."

  zone_name = "example.com"

  # Free: 5 regras customizadas na zona em vez de 20, 1 regra de rate limit em vez de 2, Bot Fight Mode em vez do
  # Super Bot Fight Mode e nenhum ruleset gerenciado configurável (o Free Managed Ruleset roda sozinho).
  plan = "pro"

  # A intenção de cada site, API e CDN. Os tetos de regras são da zona: todos os workloads dividem as mesmas 20 (5 no
  # Free). Aqui: métodos 1 (uma regra para todos os tipos) + hotlink da cdn 1 + regras do app 1 + custom_rules 1 = 4,
  # o que cabe também no Free; o plan diz quem consome o quê quando passa do teto.
  workloads = {
    site = {
      type      = "site-ssr"
      hostnames = ["example.com", "www.example.com"]
      forms     = ["/api/contato"]
      turnstile = true
    }
    app = {
      type      = "site-ssr"
      hostnames = ["app.example.com"]
      forms     = ["/api/convites"]
      rules = [{
        description = "Painel só a partir do Brasil"
        expression  = "starts_with(http.request.uri.path, \"/admin\") and ip.src.country ne \"BR\""
        action      = "block"
      }]
    }
    # API atrás da Cloudflare; o cabeçalho vem de variável sensível, nunca de um segredo escrito aqui.
    api = {
      type      = "api"
      hostnames = ["api.example.com"]
      headers   = { "X-Origin-Verify" = var.origin_verify }
    }
    cdn = {
      type             = "cdn"
      hostnames        = ["cdn.example.com"]
      bucket           = "example-cdn"
      allowed_referers = ["example.com"]
      headers          = { "Timing-Allow-Origin" = "https://example.com" }
      manage_domain    = true
    }
  }

  # Rate limit por IP. Os formulários de site e app entram numa regra só, com os hosts e as rotas de cada um. Free:
  # janela de 10 s e essa passa a ser a única regra (o limite geral some); Pro: janela de 1 minuto, ao lado do limite
  # geral. Os limiares são da zona porque, no Free, todos os workloads cabem numa regra.
  form_requests_per_period = 3

  # Limite geral por IP a cada 10 s nos sites e APIs (a cdn fica de fora), sem bots verificados. Free: só existe
  # quando nenhum workload tem forms.
  site_requests_per_period = 100

  # managed_challenge exige mitigation_timeout 0 em Free, Pro e Business (o módulo faz isso). Com block, a ação dura
  # um período: 10 s no Free, 60 s nos formulários do Pro.
  rate_limit_action = "managed_challenge"

  # Free: liga o Bot Fight Mode, que não aceita exceção (monitores podem levar desafio) e injeta um script que a CSP de
  # hashes de um site estático bloqueia. Pro: ignorado; o módulo desliga o Bot Fight Mode e liga o Super Bot Fight Mode.
  bot_fight_mode = true

  # Igual nos dois planos: busca e agentes liberados, treino bloqueado, AI Labyrinth desligado.
  ai_bots = {
    search    = "allow"
    agent     = "allow"
    training  = "block"
    labyrinth = false
  }

  # Regras da zona inteira (as de um workload vão em workloads[*].rules). Contam no mesmo teto.
  custom_rules = [{
    description = "Sondagens de arquivos que nenhum site tem"
    expression  = "http.request.uri.path in {\"/wp-login.php\" \"/xmlrpc.php\" \"/.env\"}"
    action      = "block"
  }]

  # Igual nos dois planos. Cada destino recebe um e-mail de verificação; o send_email do Worker só envia para eles.
  email_routing = {
    addresses = ["propostas@example.com"]
    rules     = [{ to = "contato@example.com", worker = "example-site" }]
  }

  # Igual nos dois planos: buckets da conta, fora os das cdn, com o dev URL r2.dev desligado.
  r2_buckets = ["example-uploads"]
}

output "turnstile_sitekeys" {
  value = module.zone.turnstile_sitekeys
}

output "turnstile_secrets" {
  value     = module.zone.turnstile_secrets
  sensitive = true
}

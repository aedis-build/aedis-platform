# Referência de todas as variáveis do módulo, numa zona fictícia (example.com) no plano Pro. Cada bloco diz o que
# muda no Free. Nada aqui é de uma zona real: troque nomes, hostnames e endereços antes de usar.
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
  description = "Valor do cabeçalho de verificação; fica no terraform.tfvars ou em TF_VAR_origin_verify, fora do git."
  type        = string
  sensitive   = true
}

module "zone" {
  # Num repositório de site: "git::https://github.com/aedis-build/aedis-platform.git//infra/cloudflare?ref=<sha da tag>"
  source = "../.."

  zone_name = "example.com"

  # Free: 5 regras customizadas em vez de 20, 1 regra de rate limit em vez de 2, Bot Fight Mode em vez do Super Bot
  # Fight Mode e nenhum ruleset gerenciado configurável (o Free Managed Ruleset roda sozinho).
  plan = "pro"

  hostnames = ["example.com", "www.example.com", "app.example.com"]

  # Rate limit no POST destas rotas, por IP. Free: janela de 10 s, e esta passa a ser a única regra (o limite geral
  # some); Pro: janela de 1 minuto, ao lado do limite geral.
  form_paths               = ["/api/contato"]
  form_requests_per_period = 3

  # Limite geral por IP a cada 10 s nos hostnames, fora bots verificados. Free: só existe com form_paths = [].
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

  # Contam no limite junto com a regra de métodos do módulo (1) e as 2 de cada CDN. Aqui: 1 + 2 + 2 = 5, o teto do
  # Free; no Free, uma terceira regra faz o plan falhar na precondição. Pro: até 20.
  custom_rules = [
    {
      description = "Painel só a partir do Brasil"
      expression  = "http.host eq \"app.example.com\" and starts_with(http.request.uri.path, \"/admin\") and ip.src.country ne \"BR\""
      action      = "block"
    },
    {
      description = "Sondagens de arquivos que o site não tem"
      expression  = "http.request.uri.path in {\"/wp-login.php\" \"/xmlrpc.php\" \"/.env\"}"
      action      = "block"
    },
  ]

  # Igual nos dois planos. O segredo do widget sai no output turnstile_secret e vai para o RUNTIME_SECRETS do
  # environment, nunca para o git.
  turnstile = { name = "example.com contato", mode = "managed" }

  # Igual nos dois planos. Cada destino recebe um e-mail de verificação; o send_email do Worker só envia para eles.
  email_routing = {
    addresses = ["propostas@example.com"]
    rules     = [{ to = "contato@example.com", worker = "example-site" }]
  }

  # Igual nos dois planos: buckets da conta com o dev URL r2.dev desligado.
  r2_buckets = ["example-uploads"]

  # Igual nos dois planos, mas no Free as 2 regras da CDN pesam mais: 2 das 5.
  cdn = [{
    hostname         = "cdn.example.com"
    bucket           = "example-cdn"
    allowed_referers = ["example.com"]
    headers          = { "Timing-Allow-Origin" = "https://example.com" }
    manage_domain    = true
  }]

  # Mesma fase dos cabeçalhos da CDN (um ruleset só por zona), igual nos dois planos.
  response_headers = [{
    description = "Verificação de origem nas respostas da app"
    expression  = "http.host eq \"app.example.com\""
    headers     = { "X-Origin-Verify" = var.origin_verify }
  }]
}

output "turnstile_sitekey" {
  value = module.zone.turnstile_sitekey
}

output "turnstile_secret" {
  value     = module.zone.turnstile_secret
  sensitive = true
}

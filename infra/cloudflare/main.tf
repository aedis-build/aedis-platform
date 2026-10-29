# Postura de segurança de uma zona da Cloudflare que serve sites do Aedis em Workers. A zona, o domínio próprio do
# Worker (routes com custom_domain no wrangler.jsonc) e os segredos do Worker ficam fora daqui: dois donos para o
# mesmo recurso brigam a cada apply.

data "cloudflare_zone" "this" {
  filter = { name = var.zone_name }
}

locals {
  zone_id    = data.cloudflare_zone.this.id
  account_id = data.cloudflare_zone.this.account.id
  pro        = var.plan == "pro"

  # Limite de regras customizadas por plano; a regra de métodos do módulo conta.
  custom_rule_limit = local.pro ? 20 : 5

  hosts      = "http.host in {${join(" ", [for h in var.hostnames : jsonencode(h)])}}"
  form_paths = "http.request.uri.path in {${join(" ", [for p in var.form_paths : jsonencode(p)])}}"

  ai_policy = { allow = "disabled", block = "block", only_on_ad_pages = "only_on_ad_pages" }

  # Rate limit: com desafio, a API do Free, Pro e Business exige mitigation_timeout 0 (o desafio zera o contador);
  # com Block, a ação dura um período.
  challenge = var.rate_limit_action == "managed_challenge"
  form_rate_limit = {
    ref         = "aedis_form_rate_limit"
    description = "POST nas rotas de formulário"
    expression  = "${local.hosts} and http.request.method eq \"POST\" and ${local.form_paths}"
    action      = var.rate_limit_action
    ratelimit = {
      characteristics     = ["cf.colo.id", "ip.src"]
      period              = local.pro ? 60 : 10
      requests_per_period = var.form_requests_per_period
      mitigation_timeout  = local.challenge ? 0 : (local.pro ? 60 : 10)
    }
  }
  # Limite geral por IP nos hostnames, fora os bots verificados (os crawlers de busca liberados pela política de IA).
  site_rate_limit = {
    ref         = "aedis_site_rate_limit"
    description = "Requisições por IP nos hostnames"
    expression  = "${local.hosts} and not cf.client.bot"
    action      = var.rate_limit_action
    ratelimit = {
      characteristics     = ["cf.colo.id", "ip.src"]
      period              = 10
      requests_per_period = var.site_requests_per_period
      mitigation_timeout  = local.challenge ? 0 : 10
    }
  }
  # O Free tem uma regra: a dos formulários quando há form_paths, senão o limite geral. O Pro tem duas: as duas.
  rate_limit_rules = length(var.form_paths) == 0 ? [local.site_rate_limit] : (local.pro ? [local.form_rate_limit, local.site_rate_limit] : [local.form_rate_limit])

  # IDs públicos dos rulesets gerenciados (iguais em toda conta).
  cloudflare_managed_ruleset = "efb7b8c949ac4650a09736fc376e9aee"
  owasp_core_ruleset         = "4814384a9e5d4991b9815dcfc25d2f1f"
}

resource "cloudflare_zone_setting" "min_tls_version" {
  zone_id    = local.zone_id
  setting_id = "min_tls_version"
  value      = "1.2"
}

resource "cloudflare_zone_setting" "always_use_https" {
  zone_id    = local.zone_id
  setting_id = "always_use_https"
  value      = "on"
}

resource "cloudflare_zone_setting" "hsts" {
  zone_id    = local.zone_id
  setting_id = "security_header"
  value = {
    strict_transport_security = {
      enabled            = true
      max_age            = 63072000
      include_subdomains = true
      preload            = true
      nosniff            = true
    }
  }
}

# Bot Fight Mode e política de IA são controles separados: liberar um crawler de IA no robots.txt não exige desligar o
# Bot Fight Mode. No Free ele desafia bots conhecidos sem aceitar exceção (README: custo para monitores e para a CSP de
# hashes). No Pro quem age é o Super Bot Fight Mode: bots verificados passam e o automatizado sem identidade é desafiado.
# A política de IA (Search, Agent, Training) vale nos dois planos: "allow" vira "disabled" (nenhum bloqueio).
resource "cloudflare_bot_management" "this" {
  zone_id                         = local.zone_id
  fight_mode                      = local.pro ? false : var.bot_fight_mode
  ai_bots_protection              = "disabled"
  aisearch                        = local.ai_policy[var.ai_bots.search]
  ai_user                         = local.ai_policy[var.ai_bots.agent]
  ai_training                     = local.ai_policy[var.ai_bots.training]
  crawler_protection              = var.ai_bots.labyrinth ? "enabled" : "disabled"
  is_robots_txt_managed           = false
  sbfm_verified_bots              = local.pro ? "allow" : null
  sbfm_definitely_automated       = local.pro ? "managed_challenge" : null
  sbfm_likely_automated           = local.pro ? "allow" : null
  sbfm_static_resource_protection = local.pro ? false : null
}

resource "cloudflare_ruleset" "custom" {
  zone_id     = local.zone_id
  kind        = "zone"
  phase       = "http_request_firewall_custom"
  name        = "aedis: regras customizadas"
  description = "Gerenciado pelo módulo infra/cloudflare do Aedis Platform"
  rules = concat([
    {
      ref         = "aedis_methods"
      description = "Sites só aceitam GET, HEAD e POST"
      expression  = "${local.hosts} and not http.request.method in {\"GET\" \"HEAD\" \"POST\"}"
      action      = "block"
    }
  ], [for r in var.custom_rules : merge(r, { ref = coalesce(r.ref, "custom_${md5(r.expression)}") })])

  lifecycle {
    precondition {
      condition     = length(var.custom_rules) + 1 <= local.custom_rule_limit
      error_message = "O plano ${var.plan} aceita ${local.custom_rule_limit} regras customizadas; a regra de métodos do módulo ocupa uma."
    }
  }
}

# Até a v1.4 o ruleset tinha count (sem form_paths não havia regra); o moved leva o estado antigo sem recriar.
moved {
  from = cloudflare_ruleset.rate_limit[0]
  to   = cloudflare_ruleset.rate_limit
}

resource "cloudflare_ruleset" "rate_limit" {
  zone_id     = local.zone_id
  kind        = "zone"
  phase       = "http_ratelimit"
  name        = "aedis: rate limit"
  description = "Gerenciado pelo módulo infra/cloudflare do Aedis Platform"
  rules       = local.rate_limit_rules
}

# No Free o Free Managed Ruleset já roda sozinho e não é configurável; o Pro executa o Cloudflare Managed e o OWASP.
resource "cloudflare_ruleset" "managed" {
  count       = local.pro ? 1 : 0
  zone_id     = local.zone_id
  kind        = "zone"
  phase       = "http_request_firewall_managed"
  name        = "aedis: rulesets gerenciados"
  description = "Gerenciado pelo módulo infra/cloudflare do Aedis Platform"
  rules = [
    {
      ref               = "aedis_cloudflare_managed"
      description       = "Cloudflare Managed Ruleset"
      expression        = "true"
      action            = "execute"
      action_parameters = { id = local.cloudflare_managed_ruleset }
    },
    {
      ref               = "aedis_owasp_core"
      description       = "Cloudflare OWASP Core Ruleset"
      expression        = "true"
      action            = "execute"
      action_parameters = { id = local.owasp_core_ruleset }
    }
  ]
}

resource "cloudflare_turnstile_widget" "this" {
  count      = var.turnstile == null ? 0 : 1
  account_id = local.account_id
  name       = coalesce(var.turnstile.name, var.zone_name)
  domains    = var.hostnames
  mode       = var.turnstile.mode
}

resource "cloudflare_email_routing_settings" "this" {
  count   = var.email_routing == null ? 0 : 1
  zone_id = local.zone_id
}

resource "cloudflare_email_routing_dns" "this" {
  count   = var.email_routing == null ? 0 : 1
  zone_id = local.zone_id
  name    = var.zone_name

  depends_on = [cloudflare_email_routing_settings.this]
}

resource "cloudflare_email_routing_address" "this" {
  for_each   = toset(try(var.email_routing.addresses, []))
  account_id = local.account_id
  email      = each.value
}

resource "cloudflare_email_routing_rule" "this" {
  for_each = { for r in try(var.email_routing.rules, []) : r.to => r }
  zone_id  = local.zone_id
  name     = each.key
  matchers = [{ type = "literal", field = "to", value = each.key }]
  actions = [{
    type  = each.value.worker != null ? "worker" : "forward"
    value = each.value.worker != null ? [each.value.worker] : each.value.forward
  }]

  depends_on = [cloudflare_email_routing_settings.this, cloudflare_email_routing_address.this]
}

resource "cloudflare_r2_bucket" "this" {
  for_each   = toset(var.r2_buckets)
  account_id = local.account_id
  name       = each.value
}

# O dev URL (r2.dev) de todo bucket fica desligado de forma explícita; nenhum custom domain de R2 é criado aqui.
resource "cloudflare_r2_managed_domain" "this" {
  for_each    = cloudflare_r2_bucket.this
  account_id  = local.account_id
  bucket_name = each.value.name
  enabled     = false
}

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

# Os crawlers de IA podem ler os sites (o robots.txt de cada site libera search, ai-input e ai-train): Bot Fight Mode
# desligado, porque nenhuma regra de skip o contorna, e nenhum bloqueio de IA. No Pro, o Super Bot Fight Mode deixa
# passar bots verificados (os crawlers de IA declarados) e desafia só o tráfego automatizado sem identidade.
resource "cloudflare_bot_management" "this" {
  zone_id                         = local.zone_id
  fight_mode                      = false
  ai_bots_protection              = "disabled"
  crawler_protection              = "disabled"
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

# A única regra de rate limit do Free: POST nas rotas de formulário, por IP. Free = janela de 10 s e Block por 10 s;
# Pro = janela de 1 min e Managed Challenge (no Free e no Pro, desafio só com mitigation_timeout 0).
resource "cloudflare_ruleset" "rate_limit" {
  count       = length(var.form_paths) > 0 ? 1 : 0
  zone_id     = local.zone_id
  kind        = "zone"
  phase       = "http_ratelimit"
  name        = "aedis: rate limit dos formulários"
  description = "Gerenciado pelo módulo infra/cloudflare do Aedis Platform"
  rules = [
    {
      ref         = "aedis_form_rate_limit"
      description = "POST nas rotas de formulário"
      expression  = "${local.hosts} and http.request.method eq \"POST\" and ${local.form_paths}"
      action      = local.pro ? "managed_challenge" : "block"
      ratelimit = {
        characteristics     = ["cf.colo.id", "ip.src"]
        period              = local.pro ? 60 : 10
        requests_per_period = var.form_requests_per_period
        mitigation_timeout  = local.pro ? 0 : 10
      }
    }
  ]
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

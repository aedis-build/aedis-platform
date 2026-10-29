# Postura de segurança de uma zona da Cloudflare a partir da intenção de cada workload (site, API ou CDN). A zona, o
# Worker, o domínio próprio do Worker (routes com custom_domain no wrangler.jsonc) e os segredos do Worker ficam fora
# daqui: dois donos para o mesmo recurso brigam a cada apply.

data "cloudflare_zone" "this" {
  filter = { name = var.zone_name }

  lifecycle {
    postcondition {
      condition     = alltrue([for h in flatten([for w in values(var.workloads) : w.hostnames]) : h == var.zone_name || endswith(h, ".${var.zone_name}")])
      error_message = "Todo hostname de workloads precisa estar na zona ${var.zone_name}."
    }
  }
}

locals {
  zone_id    = data.cloudflare_zone.this.id
  account_id = data.cloudflare_zone.this.account.id
  pro        = var.plan == "pro"

  # Regras customizadas e de rate limit são da zona, não do site: todos os workloads dividem o mesmo teto.
  custom_rule_limit = local.pro ? 20 : 5

  hosts = { for k, w in var.workloads : k => "http.host in {${join(" ", [for h in w.hostnames : jsonencode(h)])}}" }

  # Métodos aceitos por tipo. Uma regra só para a zona inteira, um termo por conjunto de métodos, para não gastar uma
  # regra do Free por workload.
  methods = {
    "site-static" = ["GET", "HEAD"]
    "cdn"         = ["GET", "HEAD"]
    "site-ssr"    = ["GET", "HEAD", "POST"]
    "api"         = ["GET", "HEAD", "POST", "PUT", "PATCH", "DELETE", "OPTIONS"]
  }
  method_groups = {
    for set in distinct([for w in values(var.workloads) : join(" ", [for m in local.methods[w.type] : jsonencode(m)])]) :
    set => flatten([for w in values(var.workloads) : w.hostnames if join(" ", [for m in local.methods[w.type] : jsonencode(m)]) == set])
  }
  methods_rule = length(var.workloads) == 0 ? [] : [{
    ref         = "aedis_methods"
    description = "Métodos aceitos por tipo de workload"
    expression  = join(" or ", [for set, hosts in local.method_groups : "(http.host in {${join(" ", [for h in hosts : jsonencode(h)])}} and not http.request.method in {${set}})"])
    action      = "block"
  }]

  # Hotlink de cada cdn: Referer vazio passa (o camo do GitHub e quem usa no-referrer); Referer de um domínio permitido
  # ou de subdomínio dele passa; o resto leva block. O wildcard (todo plano; o matches é Business) casa a URL inteira e
  # exige "/" logo depois do host, então evilaedis.build e aedis.build.evil.com não passam.
  hotlink_rules = [for k, w in var.workloads : {
    ref         = "aedis_${k}_hotlink"
    description = "${k}: hotlink de outro site"
    expression = join(" ", compact([
      "${local.hosts[k]} and http.referer ne \"\"",
      length(w.allowed_referers) == 0 ? "" : "and not (${join(" or ", flatten([for d in w.allowed_referers : ["http.referer wildcard \"https://${d}/*\"", "http.referer wildcard \"https://*.${d}/*\""]]))})",
    ]))
    action = "block"
  } if w.type == "cdn"]

  workload_rules = flatten([for k, w in var.workloads : [for r in w.rules : {
    ref         = coalesce(r.ref, "${k}_${md5(r.expression)}")
    description = "${k}: ${r.description}"
    expression  = "(${local.hosts[k]}) and (${r.expression})"
    action      = r.action
  }]])

  zone_rules = [for r in var.custom_rules : merge(r, { ref = coalesce(r.ref, "custom_${md5(r.expression)}") })]

  custom_rules = concat(local.methods_rule, local.hotlink_rules, local.workload_rules, local.zone_rules)

  # Quem consome o teto de regras customizadas, para a mensagem de erro.
  custom_rule_usage = merge(
    { "métodos (módulo)" = length(local.methods_rule) },
    { for k, w in var.workloads : "workload ${k}" => length(w.rules) + (w.type == "cdn" ? 1 : 0) },
    { "custom_rules (zona)" = length(var.custom_rules) },
  )

  # Objetos estáticos sem script nem formulário, embutidos só por páginas do mesmo site (CORP same-site).
  cdn_default_headers = {
    "Content-Security-Policy"      = "default-src 'none'; style-src 'unsafe-inline'; sandbox"
    "Cross-Origin-Resource-Policy" = "same-site"
    "Referrer-Policy"              = "no-referrer"
    "X-Content-Type-Options"       = "nosniff"
  }

  # Um ruleset por fase na zona: os cabeçalhos de resposta de todos os workloads moram no mesmo.
  response_header_rules = [for k, w in var.workloads : {
    ref               = "aedis_${k}_headers"
    description       = "${k}: cabeçalhos de resposta"
    expression        = local.hosts[k]
    action            = "rewrite"
    action_parameters = { headers = { for name, value in merge(w.type == "cdn" ? local.cdn_default_headers : {}, w.headers) : name => { operation = "set", value = value } } }
  } if w.type == "cdn" || length(w.headers) > 0]

  ai_policy = { allow = "disabled", block = "block", only_on_ad_pages = "only_on_ad_pages" }

  # Rate limit: com desafio, a API do Free, Pro e Business exige mitigation_timeout 0 (o desafio zera o contador);
  # com Block, a ação dura um período.
  challenge      = var.rate_limit_action == "managed_challenge"
  form_workloads = { for k, w in var.workloads : k => w if length(w.forms) > 0 }
  # Os formulários de todos os workloads numa regra só: no Free é a única regra de rate limit da zona.
  form_rate_limit = {
    ref         = "aedis_form_rate_limit"
    description = "POST nas rotas de formulário dos workloads"
    expression  = "http.request.method eq \"POST\" and (${join(" or ", [for k, w in local.form_workloads : "(${local.hosts[k]} and http.request.uri.path in {${join(" ", [for p in w.forms : jsonencode(p)])}})"])})"
    action      = var.rate_limit_action
    ratelimit = {
      characteristics     = ["cf.colo.id", "ip.src"]
      period              = local.pro ? 60 : 10
      requests_per_period = var.form_requests_per_period
      mitigation_timeout  = local.challenge ? 0 : (local.pro ? 60 : 10)
    }
  }
  # Limite geral por IP, fora as cdn (o camo do GitHub busca as imagens dos READMEs de poucos IPs) e fora os bots
  # verificados (os crawlers de busca liberados pela política de IA).
  site_hosts = flatten([for w in values(var.workloads) : w.hostnames if w.type != "cdn"])
  site_rate_limit = {
    ref         = "aedis_site_rate_limit"
    description = "Requisições por IP nos hostnames dos sites e APIs"
    expression  = "http.host in {${join(" ", [for h in local.site_hosts : jsonencode(h)])}} and not cf.client.bot"
    action      = var.rate_limit_action
    ratelimit = {
      characteristics     = ["cf.colo.id", "ip.src"]
      period              = 10
      requests_per_period = var.site_requests_per_period
      mitigation_timeout  = local.challenge ? 0 : 10
    }
  }
  # Free: uma regra (formulários, se algum workload os tem, senão o limite geral). Pro: as duas.
  form_rules       = length(local.form_workloads) > 0 ? [local.form_rate_limit] : []
  site_rules       = length(local.site_hosts) > 0 ? [local.site_rate_limit] : []
  rate_limit_rules = local.pro ? concat(local.form_rules, local.site_rules) : slice(concat(local.form_rules, local.site_rules), 0, min(1, length(concat(local.form_rules, local.site_rules))))

  cdn_buckets = [for w in values(var.workloads) : w.bucket if w.type == "cdn"]
  cdn_domains = merge([for w in values(var.workloads) : { for h in w.hostnames : h => w.bucket } if w.type == "cdn" && w.manage_domain]...)

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
  count       = length(local.custom_rules) > 0 ? 1 : 0
  zone_id     = local.zone_id
  kind        = "zone"
  phase       = "http_request_firewall_custom"
  name        = "aedis: regras customizadas"
  description = "Gerenciado pelo módulo infra/cloudflare do Aedis Platform"
  rules       = local.custom_rules

  lifecycle {
    precondition {
      condition     = length(local.custom_rules) <= local.custom_rule_limit
      error_message = "O plano ${var.plan} aceita ${local.custom_rule_limit} regras customizadas na zona e a configuração pede ${length(local.custom_rules)}: ${join(", ", [for who, n in local.custom_rule_usage : "${who} ${n}" if n > 0])}."
    }
  }
}

resource "cloudflare_ruleset" "response_headers" {
  count       = length(local.response_header_rules) > 0 ? 1 : 0
  zone_id     = local.zone_id
  kind        = "zone"
  phase       = "http_response_headers_transform"
  name        = "aedis: cabeçalhos de resposta"
  description = "Gerenciado pelo módulo infra/cloudflare do Aedis Platform"
  rules       = local.response_header_rules
}

resource "cloudflare_ruleset" "rate_limit" {
  count       = length(local.rate_limit_rules) > 0 ? 1 : 0
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
  for_each   = { for k, w in var.workloads : k => w if w.turnstile }
  account_id = local.account_id
  name       = "${var.zone_name} ${each.key}"
  domains    = each.value.hostnames
  mode       = "managed"
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
  for_each   = toset(concat(var.r2_buckets, local.cdn_buckets))
  account_id = local.account_id
  name       = each.value
}

# O dev URL (r2.dev) de todo bucket fica desligado de forma explícita.
resource "cloudflare_r2_managed_domain" "this" {
  for_each    = cloudflare_r2_bucket.this
  account_id  = local.account_id
  bucket_name = each.value.name
  enabled     = false
}

# Domínio próprio do bucket de cada hostname de cdn, com TLS 1.2. O provider não importa este recurso: um domínio que
# já existe no painel fica de fora com manage_domain = false até a janela de migração (README).
resource "cloudflare_r2_custom_domain" "cdn" {
  for_each    = local.cdn_domains
  account_id  = local.account_id
  zone_id     = local.zone_id
  bucket_name = cloudflare_r2_bucket.this[each.value].name
  domain      = each.key
  enabled     = true
  min_tls     = "1.2"
}

# Plan do módulo sem credenciais nem rede: o provider é simulado e a zona vem do mock.
mock_provider "cloudflare" {
  mock_data "cloudflare_zone" {
    defaults = {
      id      = "023e105f4ecef8ad9ca31a8372d0c353"
      account = { id = "f037e56e89293a057740de681ac9abbe" }
    }
  }
}

variables {
  zone_name  = "aedis.build"
  hostnames  = ["aedis.build", "ui.aedis.build"]
  form_paths = []
}

run "free_sem_formulario" {
  command = plan

  assert {
    condition     = length(cloudflare_ruleset.managed) == 0
    error_message = "o free não declara rulesets gerenciados"
  }
  assert {
    condition     = length(cloudflare_ruleset.rate_limit.rules) == 1 && cloudflare_ruleset.rate_limit.rules[0].ref == "aedis_site_rate_limit" && cloudflare_ruleset.rate_limit.rules[0].expression == "http.host in {\"aedis.build\" \"ui.aedis.build\"} and not cf.client.bot"
    error_message = "sem form_paths a única regra do free é o limite geral por IP nos hostnames, fora bots verificados"
  }
  assert {
    condition     = cloudflare_ruleset.rate_limit.rules[0].action == "managed_challenge" && cloudflare_ruleset.rate_limit.rules[0].ratelimit.mitigation_timeout == 0 && cloudflare_ruleset.rate_limit.rules[0].ratelimit.period == 10 && cloudflare_ruleset.rate_limit.rules[0].ratelimit.requests_per_period == 100
    error_message = "o limite geral desafia (mitigation_timeout 0), 100 por 10 s"
  }
  assert {
    condition     = cloudflare_ruleset.custom.rules[0].expression == "http.host in {\"aedis.build\" \"ui.aedis.build\"} and not http.request.method in {\"GET\" \"HEAD\" \"POST\"}" && cloudflare_ruleset.custom.rules[0].action == "block"
    error_message = "a regra de métodos bloqueia o que não é GET, HEAD ou POST nos hostnames"
  }
  assert {
    condition     = cloudflare_bot_management.this.fight_mode == true && cloudflare_bot_management.this.ai_bots_protection == "disabled" && cloudflare_bot_management.this.crawler_protection == "disabled"
    error_message = "Bot Fight Mode ligado por padrão, regra legada de IA e labirinto desligados"
  }
  assert {
    condition     = cloudflare_bot_management.this.aisearch == "disabled" && cloudflare_bot_management.this.ai_user == "disabled" && cloudflare_bot_management.this.ai_training == "block"
    error_message = "política de IA padrão: busca e agente liberados, treino bloqueado"
  }
  assert {
    condition     = cloudflare_zone_setting.min_tls_version.value == "1.2" && cloudflare_zone_setting.always_use_https.value == "on" && cloudflare_zone_setting.hsts.value.strict_transport_security.enabled
    error_message = "TLS 1.2, HTTPS sempre e HSTS"
  }
  assert {
    condition     = length(cloudflare_turnstile_widget.this) == 0 && length(cloudflare_email_routing_settings.this) == 0 && length(cloudflare_r2_bucket.this) == 0
    error_message = "Turnstile, Email Routing e R2 só quando pedidos"
  }
}

run "free_com_formulario_turnstile_email_r2" {
  command = plan

  variables {
    zone_name  = "rlfn.studio"
    hostnames  = ["rlfn.studio"]
    form_paths = ["/api/proposals"]
    turnstile  = {}
    email_routing = {
      addresses = ["propostas@example.com"]
      rules     = [{ to = "contato@rlfn.studio", worker = "rlfn-studio" }]
    }
    r2_buckets = ["rlfn-media", "rlfn-backups"]
  }

  assert {
    condition     = length(cloudflare_ruleset.rate_limit.rules) == 1 && cloudflare_ruleset.rate_limit.rules[0].action == "managed_challenge" && cloudflare_ruleset.rate_limit.rules[0].ratelimit.period == 10 && cloudflare_ruleset.rate_limit.rules[0].ratelimit.mitigation_timeout == 0 && cloudflare_ruleset.rate_limit.rules[0].ratelimit.requests_per_period == 3
    error_message = "no free com formulário, a única regra é a dos formulários: 3 por 10 s com desafio"
  }
  assert {
    condition     = strcontains(cloudflare_ruleset.rate_limit.rules[0].expression, "http.request.method eq \"POST\" and http.request.uri.path in {\"/api/proposals\"}")
    error_message = "o rate limit cobre o POST das form_paths"
  }
  assert {
    condition     = cloudflare_turnstile_widget.this[0].mode == "managed" && cloudflare_turnstile_widget.this[0].domains == tolist(["rlfn.studio"]) && cloudflare_turnstile_widget.this[0].name == "rlfn.studio"
    error_message = "Turnstile managed nos hostnames"
  }
  assert {
    condition     = cloudflare_email_routing_rule.this["contato@rlfn.studio"].actions[0].type == "worker" && length(cloudflare_email_routing_address.this) == 1
    error_message = "Email Routing com o destino verificado e a regra para o Worker"
  }
  assert {
    condition     = cloudflare_r2_bucket.this["rlfn-media"].account_id == "f037e56e89293a057740de681ac9abbe"
    error_message = "bucket R2 na conta da zona"
  }
  assert {
    condition     = length(cloudflare_r2_managed_domain.this) == 2 && alltrue([for d in cloudflare_r2_managed_domain.this : d.enabled == false])
    error_message = "todo bucket R2 com o dev URL (r2.dev) desligado de forma explícita"
  }
}

run "pro" {
  command = plan

  variables {
    plan       = "pro"
    form_paths = ["/api/proposals"]
  }

  assert {
    condition     = length(cloudflare_ruleset.managed) == 1 && [for r in cloudflare_ruleset.managed[0].rules : r.action_parameters.id] == ["efb7b8c949ac4650a09736fc376e9aee", "4814384a9e5d4991b9815dcfc25d2f1f"]
    error_message = "o pro executa o Cloudflare Managed e o OWASP"
  }
  assert {
    condition     = [for r in cloudflare_ruleset.rate_limit.rules : r.ref] == ["aedis_form_rate_limit", "aedis_site_rate_limit"] && cloudflare_ruleset.rate_limit.rules[0].ratelimit.period == 60 && cloudflare_ruleset.rate_limit.rules[0].ratelimit.mitigation_timeout == 0
    error_message = "o pro usa as duas regras: formulários (60 s) e limite geral, com desafio"
  }
  assert {
    condition     = cloudflare_bot_management.this.fight_mode == false && cloudflare_bot_management.this.sbfm_verified_bots == "allow"
    error_message = "no pro o Super Bot Fight Mode substitui o Bot Fight Mode e os bots verificados passam"
  }
}

run "free_aceita_cinco_regras_no_total" {
  command = plan

  variables {
    custom_rules = [for i in range(5) : { description = "r${i}", expression = "http.request.uri.path eq \"/x${i}\"", action = "block" }]
  }

  expect_failures = [cloudflare_ruleset.custom]
}

run "plano_invalido" {
  command = plan

  variables {
    plan = "business"
  }

  expect_failures = [var.plan]
}

run "regra_de_email_com_worker_e_forward" {
  command = plan

  variables {
    email_routing = {
      addresses = []
      rules     = [{ to = "a@aedis.build", worker = "w", forward = ["b@example.com"] }]
    }
  }

  expect_failures = [var.email_routing]
}

run "block_e_politicas_configuraveis" {
  command = plan

  variables {
    form_paths        = ["/api/contato"]
    rate_limit_action = "block"
    bot_fight_mode    = false
    ai_bots           = { search = "allow", agent = "only_on_ad_pages", training = "allow", labyrinth = true }
  }

  assert {
    condition     = cloudflare_ruleset.rate_limit.rules[0].action == "block" && cloudflare_ruleset.rate_limit.rules[0].ratelimit.mitigation_timeout == 10
    error_message = "com block, a ação dura um período (10 s no free)"
  }
  assert {
    condition     = cloudflare_bot_management.this.fight_mode == false && cloudflare_bot_management.this.ai_user == "only_on_ad_pages" && cloudflare_bot_management.this.ai_training == "disabled" && cloudflare_bot_management.this.crawler_protection == "enabled"
    error_message = "bot_fight_mode e ai_bots chegam ao bot_management"
  }
}

run "acao_de_rate_limit_invalida" {
  command = plan

  variables {
    rate_limit_action = "js_challenge"
  }

  expect_failures = [var.rate_limit_action]
}

run "politica_de_ia_invalida" {
  command = plan

  variables {
    ai_bots = { training = "disallow" }
  }

  expect_failures = [var.ai_bots]
}

# CDN do aedis.build: bucket R2 por domínio próprio, regras no mesmo ruleset custom e cabeçalhos de resposta.
run "cdn" {
  command = plan

  variables {
    r2_buckets = ["aedis-cdn"]
    cdn = [{
      hostname         = "cdn.aedis.build"
      bucket           = "aedis-cdn"
      allowed_referers = ["aedis.build"]
      headers          = { "Referrer-Policy" = "same-origin", "Timing-Allow-Origin" = "https://aedis.build" }
    }]
    response_headers = [{ description = "extra", expression = "http.host eq \"aedis.build\"", headers = { "X-Extra" = "1" } }]
  }

  assert {
    condition     = [for r in cloudflare_ruleset.custom.rules : r.ref] == ["aedis_methods", "aedis_cdn_methods_cdn_aedis_build", "aedis_cdn_hotlink_cdn_aedis_build"]
    error_message = "as regras da CDN entram no mesmo ruleset custom, depois da regra de métodos do módulo"
  }
  assert {
    condition     = cloudflare_ruleset.custom.rules[1].expression == "http.host eq \"cdn.aedis.build\" and not http.request.method in {\"GET\" \"HEAD\"}" && cloudflare_ruleset.custom.rules[1].action == "block"
    error_message = "a CDN só aceita GET e HEAD"
  }
  assert {
    condition     = startswith(cloudflare_ruleset.custom.rules[2].expression, "http.host eq \"cdn.aedis.build\" and http.referer ne \"\" and not (") && cloudflare_ruleset.custom.rules[2].action == "block"
    error_message = "Referer vazio passa: a regra de hotlink só olha requisições com Referer"
  }
  # O wildcard da Cloudflare (case-insensitive, * = zero ou mais caracteres, casa o valor inteiro) emulado em RE2 sobre
  # os padrões gerados: o domínio e os subdomínios passam, os parecidos não.
  assert {
    condition = alltrue([for referer in ["https://aedis.build/", "https://aedis.build/docs/x", "https://ui.aedis.build/", "https://a.b.aedis.build/p", "https://AEDIS.build/"] :
      anytrue([for p in flatten(regexall("wildcard \"([^\"]+)\"", cloudflare_ruleset.custom.rules[2].expression)) : can(regex("^(?i)${replace(replace(p, ".", "\\."), "*", ".*")}$", referer))])
    ])
    error_message = "Referer do domínio permitido ou de subdomínio dele precisa passar"
  }
  assert {
    condition = alltrue([for referer in ["https://evilaedis.build/", "https://aedis.build.evil.com/", "https://evil.com/", "http://aedis.build/", "https://aedis.builder/"] :
      !anytrue([for p in flatten(regexall("wildcard \"([^\"]+)\"", cloudflare_ruleset.custom.rules[2].expression)) : can(regex("^(?i)${replace(replace(p, ".", "\\."), "*", ".*")}$", referer))])
    ])
    error_message = "domínio parecido ou de outro site não passa"
  }
  assert {
    condition     = length(cloudflare_r2_bucket.this) == 1 && cloudflare_r2_managed_domain.this["aedis-cdn"].enabled == false
    error_message = "o bucket da CDN não duplica com r2_buckets e fica com o dev URL desligado"
  }
  assert {
    condition     = cloudflare_r2_custom_domain.cdn["cdn.aedis.build"].domain == "cdn.aedis.build" && cloudflare_r2_custom_domain.cdn["cdn.aedis.build"].bucket_name == "aedis-cdn" && cloudflare_r2_custom_domain.cdn["cdn.aedis.build"].enabled && cloudflare_r2_custom_domain.cdn["cdn.aedis.build"].min_tls == "1.2"
    error_message = "domínio próprio do R2 na zona, com TLS 1.2"
  }
  assert {
    condition     = cloudflare_ruleset.response_headers[0].phase == "http_response_headers_transform" && length(cloudflare_ruleset.response_headers[0].rules) == 2 && cloudflare_ruleset.response_headers[0].rules[0].expression == "http.host eq \"cdn.aedis.build\""
    error_message = "um ruleset só na fase de cabeçalhos de resposta, com a regra da CDN e a extra"
  }
  assert {
    condition = (cloudflare_ruleset.response_headers[0].rules[0].action_parameters.headers["Content-Security-Policy"].value == "default-src 'none'; style-src 'unsafe-inline'; sandbox"
      && cloudflare_ruleset.response_headers[0].rules[0].action_parameters.headers["Cross-Origin-Resource-Policy"].value == "same-site"
      && cloudflare_ruleset.response_headers[0].rules[0].action_parameters.headers["X-Content-Type-Options"].value == "nosniff"
      && cloudflare_ruleset.response_headers[0].rules[0].action_parameters.headers["Referrer-Policy"].value == "same-origin"
    && cloudflare_ruleset.response_headers[0].rules[0].action_parameters.headers["Timing-Allow-Origin"].operation == "set")
    error_message = "cabeçalhos padrão da CDN, com os de headers sobrepondo ou somando"
  }
  assert {
    condition     = cloudflare_ruleset.response_headers[0].rules[1].action_parameters.headers["X-Extra"].value == "1" && cloudflare_ruleset.response_headers[0].rules[1].action == "rewrite"
    error_message = "cabeçalhos extras por expressão no mesmo ruleset"
  }
}

run "cdn_sem_referer_permitido_so_aceita_referer_vazio" {
  command = plan

  variables {
    cdn = [{ hostname = "cdn.aedis.build", bucket = "b", allowed_referers = [] }]
  }

  assert {
    condition     = cloudflare_ruleset.custom.rules[2].expression == "http.host eq \"cdn.aedis.build\" and http.referer ne \"\""
    error_message = "sem allowed_referers, só Referer vazio passa"
  }
}

run "cdn_com_dominio_fora_do_terraform" {
  command = plan

  variables {
    cdn = [{ hostname = "cdn.aedis.build", bucket = "aedis-cdn", allowed_referers = ["aedis.build"], manage_domain = false }]
  }

  assert {
    condition     = length(cloudflare_r2_custom_domain.cdn) == 0 && cloudflare_r2_managed_domain.this["aedis-cdn"].enabled == false && length(cloudflare_ruleset.custom.rules) == 3
    error_message = "manage_domain = false tira só o domínio próprio; dev URL, regras e cabeçalhos continuam"
  }
}

run "cdn_estoura_o_limite_do_free" {
  command = plan

  variables {
    cdn          = [{ hostname = "cdn.aedis.build", bucket = "b", allowed_referers = ["aedis.build"] }]
    custom_rules = [for i in range(3) : { description = "r${i}", expression = "http.request.uri.path eq \"/x${i}\"", action = "block" }]
  }

  expect_failures = [cloudflare_ruleset.custom]
}

run "cdn_fora_da_zona" {
  command = plan

  variables {
    cdn = [{ hostname = "cdn.outro.example", bucket = "b", allowed_referers = ["aedis.build"] }]
  }

  expect_failures = [cloudflare_r2_custom_domain.cdn]
}

run "cdn_referer_com_esquema" {
  command = plan

  variables {
    cdn = [{ hostname = "cdn.aedis.build", bucket = "b", allowed_referers = ["https://aedis.build"] }]
  }

  expect_failures = [var.cdn]
}

run "sem_cdn_nem_extras_nao_ha_ruleset_de_cabecalhos" {
  command = plan

  assert {
    condition     = length(cloudflare_ruleset.response_headers) == 0 && length(cloudflare_r2_custom_domain.cdn) == 0
    error_message = "a fase de cabeçalhos de resposta só é tocada quando há CDN ou extras"
  }
}

# O exemplo de referência (examples/complete) planeja sem credenciais, com todas as variáveis e o limite do plano.
run "exemplo_complete" {
  command = plan

  module {
    source = "./examples/complete"
  }

  variables {
    origin_verify = "valor-de-teste"
  }

  assert {
    condition     = length(module.zone.zone_id) > 0
    error_message = "o exemplo completo planeja"
  }
}

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
    condition     = length(cloudflare_ruleset.rate_limit) == 0 && length(cloudflare_ruleset.managed) == 0
    error_message = "sem form_paths não há rate limit, e o free não declara rulesets gerenciados"
  }
  assert {
    condition     = cloudflare_ruleset.custom.rules[0].expression == "http.host in {\"aedis.build\" \"ui.aedis.build\"} and not http.request.method in {\"GET\" \"HEAD\" \"POST\"}" && cloudflare_ruleset.custom.rules[0].action == "block"
    error_message = "a regra de métodos bloqueia o que não é GET, HEAD ou POST nos hostnames"
  }
  assert {
    condition     = cloudflare_bot_management.this.fight_mode == false && cloudflare_bot_management.this.ai_bots_protection == "disabled"
    error_message = "Bot Fight Mode desligado e IA liberada"
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
    r2_buckets = ["rlfn-media"]
  }

  assert {
    condition     = cloudflare_ruleset.rate_limit[0].rules[0].action == "block" && cloudflare_ruleset.rate_limit[0].rules[0].ratelimit.period == 10 && cloudflare_ruleset.rate_limit[0].rules[0].ratelimit.mitigation_timeout == 10
    error_message = "no free o rate limit é 10 s e Block"
  }
  assert {
    condition     = strcontains(cloudflare_ruleset.rate_limit[0].rules[0].expression, "http.request.method eq \"POST\" and http.request.uri.path in {\"/api/proposals\"}")
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
    condition     = cloudflare_ruleset.rate_limit[0].rules[0].action == "managed_challenge" && cloudflare_ruleset.rate_limit[0].rules[0].ratelimit.period == 60 && cloudflare_ruleset.rate_limit[0].rules[0].ratelimit.mitigation_timeout == 0
    error_message = "no pro o rate limit desafia (mitigation_timeout 0, exigido para desafio)"
  }
  assert {
    condition     = cloudflare_bot_management.this.fight_mode == false && cloudflare_bot_management.this.sbfm_verified_bots == "allow"
    error_message = "no pro os bots verificados (crawlers de IA) passam"
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

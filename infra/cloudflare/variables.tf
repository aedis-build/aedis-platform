variable "zone_name" {
  description = "Zona que já existe na conta (ex. aedis.build). O módulo não cria nem apaga a zona."
  type        = string
}

variable "plan" {
  description = "Plano da zona: free ou pro. Decide o limite de regras, o rate limit e os rulesets gerenciados."
  type        = string
  default     = "free"

  validation {
    condition     = contains(["free", "pro"], var.plan)
    error_message = "plan deve ser free ou pro."
  }
}

variable "form_requests_per_period" {
  description = "POSTs por IP aceitos nas rotas de forms dos workloads a cada período (10 s no free, 60 s no pro)."
  type        = number
  default     = 3
}

variable "site_requests_per_period" {
  description = <<-EOT
    Limite geral: requisições por IP a cada 10 s nos hostnames dos workloads que não são cdn (todas as rotas e métodos,
    fora bots verificados). É a regra do Free quando nenhum workload tem forms; no Pro vale sempre. 100 = 10 req/s sustentados, folga para a primeira carga
    de uma página (HTML, chunks, CSS, fontes, imagens e prefetch de links) e para vários visitantes atrás do mesmo NAT.
  EOT
  type        = number
  default     = 100
}

variable "rate_limit_action" {
  description = "Ação do rate limit: managed_challenge (padrão; quem é gente passa pelo desafio) ou block."
  type        = string
  default     = "managed_challenge"

  validation {
    condition     = contains(["managed_challenge", "block"], var.rate_limit_action)
    error_message = "rate_limit_action deve ser managed_challenge ou block."
  }
}

variable "bot_fight_mode" {
  description = "Bot Fight Mode no Free (no Pro o Super Bot Fight Mode o substitui). Sem exceção possível: veja o custo no README."
  type        = bool
  default     = true
}

variable "ai_bots" {
  description = <<-EOT
    Política de IA por comportamento (allow, block ou only_on_ad_pages): search (busca), agent (agentes e chat agindo
    por uma pessoa) e training (treino de modelo, inclusive crawlers mistos busca+treino). labyrinth liga o AI
    Labyrinth (links falsos para crawlers que ignoram o robots.txt).
  EOT
  type = object({
    search    = optional(string, "allow")
    agent     = optional(string, "allow")
    training  = optional(string, "block")
    labyrinth = optional(bool, false)
  })
  default = {}

  validation {
    condition     = alltrue([for p in [var.ai_bots.search, var.ai_bots.agent, var.ai_bots.training] : contains(["allow", "block", "only_on_ad_pages"], p)])
    error_message = "cada política de ai_bots é allow, block ou only_on_ad_pages."
  }
}

variable "custom_rules" {
  description = "Regras de WAF da zona inteira (as de um site vão em workloads[*].rules); o total da zona respeita o plano (5 no free, 20 no pro)."
  type = list(object({
    description = string
    expression  = string
    action      = string
    ref         = optional(string)
  }))
  default = []
}

variable "email_routing" {
  description = <<-EOT
    Email Routing da zona; null = desligado. addresses: destinos verificados (cada um recebe um e-mail de verificação;
    o binding send_email do Worker só envia para eles). rules: endereço da zona → Worker ou encaminhamento.
  EOT
  type = object({
    addresses = list(string)
    rules = optional(list(object({
      to      = string
      worker  = optional(string)
      forward = optional(list(string))
    })), [])
  })
  default = null

  validation {
    condition     = var.email_routing == null || alltrue([for r in try(var.email_routing.rules, []) : (r.worker == null) != (r.forward == null)])
    error_message = "cada regra do email_routing tem worker ou forward, nunca os dois."
  }
}

variable "r2_buckets" {
  description = "Buckets R2 da conta usados pelos sites (ex. mídia); [] = nenhum."
  type        = list(string)
  default     = []
}

variable "workloads" {
  description = <<-EOT
    Declaração de intenção de cada site ou API da zona, uma chave por workload; a mesma estrutura serve ao módulo de
    outro provedor. O módulo não cria Worker nem domínio de Worker: o wrangler e o deploy-site seguem donos deles.
      type        site-static (GET e HEAD), site-ssr (mais POST), api (mais PUT, PATCH, DELETE e OPTIONS) ou cdn
                  (bucket R2 servido por domínio próprio, GET e HEAD)
      hostnames   os hostnames do workload; toda regra gerada fica restrita a eles
      forms       rotas que recebem POST (rate limit); só site-ssr e api
      turnstile   um widget do Turnstile para os hostnames; não vale para cdn
      headers     cabeçalhos de resposta (na cdn, sobrepõem ou somam aos de segurança padrão)
      rules       regras de WAF só deste workload (a expressão é combinada com os hostnames)
      bucket, allowed_referers, manage_domain   só cdn: bucket R2, domínios cujo Referer passa no hotlink e se o
                  domínio próprio do R2 é do Terraform (o provider não o importa)
  EOT
  type = map(object({
    type      = string
    hostnames = list(string)
    forms     = optional(list(string), [])
    turnstile = optional(bool, false)
    headers   = optional(map(string), {})
    rules = optional(list(object({
      description = string
      expression  = string
      action      = string
      ref         = optional(string)
    })), [])
    bucket           = optional(string)
    allowed_referers = optional(list(string), [])
    manage_domain    = optional(bool, true)
  }))
  default = {}

  validation {
    condition     = alltrue([for w in values(var.workloads) : contains(["site-static", "site-ssr", "api", "cdn"], w.type) && length(w.hostnames) > 0])
    error_message = "cada workload tem type site-static, site-ssr, api ou cdn e pelo menos um hostname."
  }

  validation {
    condition     = length(flatten([for w in values(var.workloads) : w.hostnames])) == length(distinct(flatten([for w in values(var.workloads) : w.hostnames])))
    error_message = "um hostname pertence a um workload só."
  }

  validation {
    condition     = alltrue([for w in values(var.workloads) : (length(w.forms) == 0 || contains(["site-ssr", "api"], w.type)) && alltrue([for p in w.forms : startswith(p, "/")])])
    error_message = "forms só em workloads site-ssr ou api (um export estático e uma cdn não recebem POST), e cada rota começa com /."
  }

  validation {
    condition     = alltrue([for w in values(var.workloads) : w.type == "cdn" ? (w.bucket != null && !w.turnstile) : (w.bucket == null && length(w.allowed_referers) == 0)])
    error_message = "workload cdn exige bucket e não tem turnstile; bucket e allowed_referers são só de cdn."
  }

  validation {
    condition     = alltrue(flatten([for w in values(var.workloads) : [for d in w.allowed_referers : can(regex("^[a-z0-9]([-a-z0-9]*[a-z0-9])?(\\.[a-z0-9]([-a-z0-9]*[a-z0-9])?)+$", d))]]))
    error_message = "allowed_referers são domínios (ex. aedis.build), sem esquema, caminho nem curinga."
  }
}

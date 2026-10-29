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

variable "hostnames" {
  description = "Hostnames servidos pelos Workers da zona (os custom domains do wrangler.jsonc); as regras valem só para eles."
  type        = list(string)

  validation {
    condition     = length(var.hostnames) > 0
    error_message = "hostnames precisa de pelo menos um hostname."
  }
}

variable "form_paths" {
  description = "Rotas que recebem POST de formulário (ex. /api/proposals), cobertas pelo rate limit. Sem formulário, [] explícito."
  type        = list(string)

  validation {
    condition     = alltrue([for p in var.form_paths : startswith(p, "/")])
    error_message = "cada form_path começa com /."
  }
}

variable "form_requests_per_period" {
  description = "POSTs por IP aceitos nas form_paths a cada período (10 s no free, 60 s no pro) antes da ação do rate limit."
  type        = number
  default     = 3
}

variable "site_requests_per_period" {
  description = <<-EOT
    Limite geral: requisições por IP a cada 10 s nos hostnames (todas as rotas e métodos, fora bots verificados). É a
    regra do Free quando form_paths = []; no Pro vale sempre. 100 = 10 req/s sustentados, folga para a primeira carga
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
  description = "Regras customizadas de WAF além da regra de métodos do módulo; o total respeita o plano (5 no free, 20 no pro)."
  type = list(object({
    description = string
    expression  = string
    action      = string
    ref         = optional(string)
  }))
  default = []
}

variable "turnstile" {
  description = "Widget do Turnstile para os hostnames; null = sem widget."
  type = object({
    name = optional(string)
    mode = optional(string, "managed")
  })
  default = null
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

variable "cdn" {
  description = <<-EOT
    CDNs servidas direto de um bucket R2 por domínio próprio, sem Worker. Para cada uma: o bucket (com o dev URL
    r2.dev desligado), o domínio próprio na zona, duas regras no ruleset custom (só GET e HEAD; hotlink: Referer vazio
    ou de allowed_referers e subdomínios passa, o resto leva block) e os cabeçalhos de segurança da CDN, com headers
    sobrepondo ou somando. manage_domain = false deixa o domínio fora do Terraform (o provider não o importa).
  EOT
  type = list(object({
    hostname         = string
    bucket           = string
    allowed_referers = list(string)
    headers          = optional(map(string), {})
    manage_domain    = optional(bool, true)
  }))
  default = []

  validation {
    condition     = alltrue(flatten([for c in var.cdn : [for d in c.allowed_referers : can(regex("^[a-z0-9]([-a-z0-9]*[a-z0-9])?(\\.[a-z0-9]([-a-z0-9]*[a-z0-9])?)+$", d))]]))
    error_message = "allowed_referers são domínios (ex. aedis.build), sem esquema, caminho nem curinga."
  }
}

variable "response_headers" {
  description = <<-EOT
    Cabeçalhos de resposta extras, por expressão, no mesmo ruleset da fase http_response_headers_transform que leva os
    da CDN: a fase tem um ruleset só por zona, então todo cabeçalho de resposta da zona passa por aqui.
  EOT
  type = list(object({
    description = string
    expression  = string
    headers     = map(string)
    ref         = optional(string)
  }))
  default = []
}

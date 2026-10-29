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
  default     = 5
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

# rlfn.studio: Next SSR (OpenNext) com o formulário de propostas em /api/proposals, Turnstile no formulário e o
# binding send_email do Worker entregando as propostas num endereço verificado do Email Routing. Plano free.
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

variable "proposals_inbox" {
  description = "Caixa que recebe as propostas (destino verificado do send_email); fica no terraform.tfvars, fora do git."
  type        = string
}

module "zone" {
  # Num repositório de site: "git::https://github.com/aedis-build/aedis-platform.git//infra/cloudflare?ref=<sha da tag>"
  source = "../.."

  zone_name = "rlfn.studio"
  plan      = "free"

  workloads = {
    site = {
      type      = "site-ssr"
      hostnames = ["rlfn.studio", "staging.rlfn.studio"]
      forms     = ["/api/proposals"]
      turnstile = true
    }
  }

  email_routing = {
    addresses = [var.proposals_inbox]
  }
}

output "turnstile_sitekey" {
  value = module.zone.turnstile_sitekeys["site"]
}

output "turnstile_secret" {
  value     = module.zone.turnstile_secrets["site"]
  sensitive = true
}

# aedis.build e ui.aedis.build: sites estáticos na mesma zona, sem formulário. Plano free.
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

module "zone" {
  # Num repositório de site: "git::https://github.com/aedis-build/aedis-platform.git//infra/cloudflare?ref=<sha da tag>"
  source = "../.."

  zone_name  = "aedis.build"
  plan       = "free"
  hostnames  = ["aedis.build", "ui.aedis.build"]
  form_paths = []
}

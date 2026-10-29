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

  zone_name = "aedis.build"
  plan      = "free"
  hostnames = ["aedis.build", "ui.aedis.build"]
  # Sem formulário: a única regra de rate limit do Free vira o limite geral por IP nos dois hostnames.
  form_paths = []

  # Imagens e arquivos estáticos servidos direto do bucket R2, sem Worker. Ocupa 2 das 5 regras customizadas do Free.
  cdn = [{
    hostname         = "cdn.aedis.build"
    bucket           = "aedis-cdn"
    allowed_referers = ["aedis.build"]
    # O domínio já existe no painel e o provider não o importa: fica fora até a janela de migração (README do módulo).
    manage_domain = false
  }]
}

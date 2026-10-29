output "zone_id" {
  description = "ID da zona."
  value       = local.zone_id
}

output "account_id" {
  description = "ID da conta dona da zona."
  value       = local.account_id
}

output "turnstile_sitekey" {
  description = "Sitekey pública do Turnstile (vai no HTML do formulário); null sem widget."
  value       = try(cloudflare_turnstile_widget.this[0].sitekey, null)
}

output "turnstile_secret" {
  description = "Segredo do Turnstile para o siteverify do Worker: vai para o RUNTIME_SECRETS do environment, nunca para o git."
  value       = try(cloudflare_turnstile_widget.this[0].secret, null)
  sensitive   = true
}

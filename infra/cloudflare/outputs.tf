output "zone_id" {
  description = "ID da zona."
  value       = local.zone_id
}

output "account_id" {
  description = "ID da conta dona da zona."
  value       = local.account_id
}

output "turnstile_sitekeys" {
  description = "Sitekey pública do Turnstile por workload (vai no HTML do formulário)."
  value       = { for k, w in cloudflare_turnstile_widget.this : k => w.sitekey }
}

output "turnstile_secrets" {
  description = "Segredo do Turnstile por workload, para o siteverify do Worker: vai para o RUNTIME_SECRETS do environment, nunca para o git."
  value       = { for k, w in cloudflare_turnstile_widget.this : k => w.secret }
  sensitive   = true
}

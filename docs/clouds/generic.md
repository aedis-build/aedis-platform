# DigitalOcean (DOKS) e Kubernetes genérico

Perfil sem cofre nativo, sem KMS e sem identidade de pod: tudo o que é "da nuvem" vem de fora.

| Peça | Como |
|---|---|
| Identidade | `identity.enabled: false`. Credenciais de APIs externas (Spaces, SaaS) vêm do cofre como segredos |
| Segredos | HashiCorp Vault (no cluster ou HCP Vault) via ESO com autenticação Kubernetes: `ClusterSecretStore aedis-secrets` com `provider.vault` e `auth.kubernetes`. `secrets.provider: vault`, `prefix: apps/prod`, `key: orders-api`, `property` obrigatório. Alternativas suportadas pelo ESO: Doppler, Infisical, 1Password, Bitwarden (token) |
| Registry | GHCR (recomendado: login por OIDC) ou DOCR. A DigitalOcean **não suporta OIDC do GitHub**: o login no DOCR usa um token de API escopado guardado como secret do GitHub — exceção documentada à regra "só OIDC" |
| Ingress | `ingress.preset: nginx` (ou `do-nginx`, idêntico). Na DO o load balancer é anotado no Service do ingress-nginx (`service.beta.kubernetes.io/do-loadbalancer-*`), não no Ingress da app. WAF: externo (Cloudflare) ou `edge.waf: false` |
| KEDA | `queues[].kind: rabbitmq` (`hostFromEnv` aponta para a variável com a URL `amqp://` vinda do Secret) e `cron` |
| Rede | VPC da DO é 10.x; metadados em 169.254.169.254: baseline anti-SSRF se aplica sem mudança |
| Assinatura HTTP | `Signing.InProcess` (chave PEM do cofre) ou, no futuro, `Aedis.Signing.VaultTransit` |
| Canário | Prometheus (default) |

`platform/values/prod.yaml` típico:

```yaml
environment: prod
environmentClass: prod
cloud: { provider: digitalocean, region: nyc3 }
secrets: { provider: vault, prefix: apps/prod }
ingress:
  preset: do-nginx
  proxyNetworks: [10.10.0.0/16]
```

Para **kind** (CI e desenvolvimento) use `cloud.provider: generic`, `rollout.strategy: rolling` ou instale
Argo Rollouts, e o provider `fake` do ESO para simular o cofre.

# Azure (AKS)

| Peça | Como |
|---|---|
| Identidade | Azure Workload Identity: `identity.enabled: true`, `identity.azure.clientId` (+ `tenantId`). O chart anota a ServiceAccount e marca o pod com `azure.workload.identity/use`. A federated credential da managed identity aponta para `system:serviceaccount:<namespace>:<name>` |
| Segredos | Azure Key Vault via ESO: `ClusterSecretStore aedis-secrets` com `provider.azurekv` e autenticação `WorkloadIdentity`. Nomes de segredo só aceitam `[A-Za-z0-9-]`: `secrets.prefix: prod` + `key: notifications/db` → `prod-notifications-db` |
| Registry | ACR (`build-image.yml` com `registry: acr`, `azure/login` com federated credential) ou GHCR |
| Ingress | `ingress.preset: agic` (Application Gateway Ingress Controller, `agic.sslCertificate`) ou `nginx` |
| KEDA | `queues[].kind: azure-servicebus` (`queueName` ou `topicName`+`subscriptionName`, `namespaceFromEnv`) ou `azure-queue`; TriggerAuthentication `podIdentity: azure-workload` com o `clientId` |
| Rede | `ingress.proxyNetworks` e `networkPolicy.egress.allowedCidrs` = CIDR da VNet; o bloqueio de 169.254.169.254 e 168.63.129.16 cobre IMDS e wireserver |
| Assinatura HTTP | `Signing.InProcess` hoje; `Aedis.Signing.AzureKeyVault` é flag em aberto no framework |

`platform/values/staging.yaml` típico:

```yaml
environment: staging
environmentClass: nonprod
cloud: { provider: azure, region: brazilsouth }
secrets: { provider: azure, prefix: staging }
ingress:
  preset: agic
  proxyNetworks: [10.1.0.0/16]
observability:
  otlp: { endpoint: http://otel-collector.observability:4317 }
```

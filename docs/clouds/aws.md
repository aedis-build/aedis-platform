# AWS (EKS)

| Peça | Como |
|---|---|
| Identidade | IRSA: `identity.enabled: true`, `identity.aws.accountId` (+ `roleName`, default `<name>`) ou `roleArn`. A trust policy da role aponta para `system:serviceaccount:<namespace>:<name>` |
| Segredos | AWS Secrets Manager via ESO: `ClusterSecretStore aedis-secrets` com `provider.aws.service: SecretsManager` e a role do ESO (IRSA). `secrets.prefix: prod` → segredo `prod/<key>`; `property` = chave JSON; `dataFrom` para um segredo JSON por app |
| Registry | ECR (`build-image.yml` com `registry: ecr`, login via OIDC `aws-actions/configure-aws-credentials`) ou GHCR |
| Ingress | `ingress.preset: alb` (AWS Load Balancer Controller, target-type ip, ACM pelo host ou `certificateArn`, external-dns) ou `nginx` atrás de NLB |
| KEDA | `queues[].kind: sqs` (`queueUrlFromEnv`, `region` ou `cloud.region`), TriggerAuthentication `podIdentity: aws` com a mesma role IRSA |
| Rede | `ingress.proxyNetworks` e `networkPolicy.egress.allowedCidrs` = CIDR da VPC; o bloqueio de 169.254.169.254 cobre o IMDS |
| Assinatura HTTP | `Aedis.Signing.AwsKms` (`secrets.keys` com `Signing__AwsKms__KeyId`) |

`platform/values/prod.yaml` típico:

```yaml
environment: prod
environmentClass: prod
cloud: { provider: aws, region: sa-east-1 }
secrets: { provider: aws, prefix: prod }
ingress:
  preset: alb
  proxyNetworks: [10.64.0.0/16]
  alb: { scheme: internet-facing, groupName: prod-public }
observability:
  otlp: { endpoint: http://otel-collector.observability:4317 }
networkPolicy:
  egress: { allowedCidrs: [10.64.0.0/16] }
```

Permissões mínimas: a role IRSA do app só precisa do que a aplicação usa (SQS, KMS, S3); o ESO lê os
segredos com a role dele (`secretsmanager:GetSecretValue` nos prefixos dos ambientes). Nenhuma chave estática.

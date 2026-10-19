# Referência do chart `aedis-app`

Values em três camadas (mapas mesclam, listas substituem): `platform/values/<env>.yaml` → `apps/<app>/app.yaml`
→ `apps/<app>/env/<env>.yaml`. O `values.schema.json` rejeita chaves desconhecidas no topo.

## Identidade do app

| Chave | Descrição |
|---|---|
| `name`, `namespace` | obrigatórios; `name` é o nome do diretório em `apps/` |
| `type` | `api` (Rollout + Service + Ingress), `worker` (Rollout + Service de métricas), `cronjob`, `site` (como `api`, sem as variáveis do framework Aedis e com probes em `/`) |
| `environment` | nome livre do ambiente (labels, `deployment.environment`) |
| `environmentClass` | `nonprod` \| `prod` — decide `ASPNETCORE_ENVIRONMENT`, HSTS e a exigência de `image.digest` |
| `cloud.provider`, `cloud.region` | `aws` \| `azure` \| `digitalocean` \| `generic`; define defaults de identidade, cofre e KEDA |
| `catalog` | bloco livre para portal; não renderiza nada |

## Imagem

`image.repository` aceita qualquer registry OCI. `image.tag` é semver; `image.digest` (`sha256:…`) tem
precedência e é obrigatório em `environmentClass: prod` (L2+). `image.pullSecrets` lista Secrets de pull.

## Baseline de segurança (sempre)

`runAsNonRoot`, `runAsUser/fsGroup = security.uid` (1654 = `$APP_UID` das imagens chiseled do .NET),
seccomp `RuntimeDefault`, `allowPrivilegeEscalation: false`, `readOnlyRootFilesystem: true`, `capabilities.drop: [ALL]`,
`/tmp` em emptyDir (`security.tmpSizeLimit`), `security.writablePaths[]` para outros caminhos, requests/limits,
`automountServiceAccountToken: false` (`identity.automountToken`). Um preset nunca altera o baseline.

## Identidade de workload (`identity`)

| Nuvem | Rende |
|---|---|
| `aws` | `eks.amazonaws.com/role-arn` na ServiceAccount (`identity.aws.roleArn`, ou `accountId` + `roleName`, default `<name>`) |
| `azure` | `azure.workload.identity/client-id` (+ `tenant-id`) na ServiceAccount e label `azure.workload.identity/use` no pod |
| outros | `identity.enabled` falha: use `identity.annotations` ou credenciais do cofre |

## Segredos (`secrets`)

Gera `ExternalSecret <name>` → `Secret <name>-secret` (envFrom). `secrets.provider` (default por nuvem) define
o `remoteRef.key`:

| Provider | `remoteRef.key` | `property` |
|---|---|---|
| `aws` | `<prefix>/<key>` | chave JSON do segredo (opcional) |
| `azure` | `<prefix>-<key>` (caracteres fora de `[A-Za-z0-9-]` viram `-`) | opcional |
| `vault` | `<prefix>/<key>` (KV v2) | obrigatório |
| `generic` | `<prefix>/<key>` | opcional |

`secrets.dataFrom[]` despeja um segredo JSON inteiro em variáveis. `secrets.extra[]` cria ExternalSecrets
adicionais (`<name>-<extra>`). `secrets.keyTemplate` (tpl com `.prefix`, `.key`, `.provider`) sobrescreve a convenção.
`secrets.runtime` injeta `AwsSecretsManager__Region` / `AzureKeyVault__VaultUri` / `Vault__Address` para apps
que leem o cofre em runtime com `Aedis.Secrets`.

## Ingress (`ingress`, só `type: api` e `site`)

`preset`: `nginx` (default), `alb`, `agic`, `kong`, `do-nginx`, `none`. Cada preset injeta a `ingressClassName` e
as anotações do controlador; `ingress.annotations` é mesclado por último. `ingress.hosts` alimenta também
`Security__HostHeaders__AllowedHosts`. `ingress.proxyNetworks` alimenta `KnownNetworks`. `ingress.edge.waf`
é uma declaração lida pelo DAST e pela conformidade.

- **alb**: `ingress.alb.{scheme, groupName, certificateArn, sslPolicy, subnets, inboundCidrs, healthcheck}`; healthcheck na readiness.
- **agic**: `ingress.agic.{sslCertificate, backendProtocol}`; sonda na readiness.
- **kong**: `ingress.routes[] {path, methods, plugins}` → uma Ingress por rota com `konghq.com/methods`;
  `ingress.kong.{upstream, plugins, defaultPlugins, stripPath}` → `KongUpstreamPolicy` e `KongPlugin`s.

## Escala (`autoscaling`)

`hpa` por CPU ou `keda` com `queues[]` (`sqs`, `azure-servicebus`, `azure-queue`, `rabbitmq`), `triggers[]` livres,
`businessHours` (só worker: fora do horário vai a zero). SQS/Azure exigem `identity.enabled` (TriggerAuthentication
com `podIdentity` aws ou azure-workload). APIs têm piso de réplicas `availability.apiMinReplicas` (2).

## Rollout (`rollout`)

`strategy: canary` (Argo Rollouts, `steps`, análise em background a partir do 1º passo) ou `rolling` (Deployment).
`analysis.provider` (`prometheus` default, `datadog`, `newrelic`, `cloudwatch`, `web`, `none`) define os args passados
aos `ClusterAnalysisTemplates` `aedis-http-success-rate`, `aedis-http-p95-latency`, `aedis-pod-restarts`
(instalados por `charts/aedis-cluster` para o backend escolhido).

## Observabilidade (`observability`)

`metrics` (porta 8081 → ServiceMonitor), `otlp` (endpoint, protocolo, chave de API via Secret), `resourceAttributes`,
`vendor.name` (`datadog`: labels de unified service tagging + `DD_*`; `dynatrace`: anotações OneAgent + `DT_*`;
`splunk`: anotação de instrumentação; `newrelic`: `NEW_RELIC_APP_NAME`).

## Rede (`networkPolicy`)

`egress.enabled`: DNS + `allowedCidrs` (VPC/VNet) + internet menos `blockedCidrs` (metadados 169.254/16,
wireserver 168.63.129.16, RFC1918, loopback, CGNAT). `ingress.enabled`: só a porta do container, do próprio
namespace, dos namespaces de monitoração e do controlador, e de `sourceCidrs`.

## Hooks (`hooks`)

`jobs[]` (PreSync/Sync/PostSync, com o baseline e a env do app) e `cleanup{}` (Job PostSync com Role mínima que
apaga objetos legados por nome: deployments, HPAs, VPAs, scaledobjects, triggerauthentications, servicemonitors,
externalsecrets).

## Exemplos

`charts/aedis-app/ci/*.yaml` cobre: API genérica com nginx e Vault; API na AWS com IRSA, ALB, Secrets Manager e
Datadog; worker no Azure com Workload Identity, Key Vault e KEDA Service Bus; worker genérico com RabbitMQ
e Deployment; CronJob com cleanup; API no Kong; API no AKS com AGIC.

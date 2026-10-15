# aedis-cli

Ferramenta `dotnet tool` do Aedis Platform.

```bash
dotnet tool install -g aedis-cli
aedis platform init --cloud aws --org minha-org --envs dev,prod
aedis platform new-app orders-api --type api --namespace shop --from-project ./src/Orders.Api
aedis platform validate --level 1
```

| Comando | Faz |
|---|---|
| `platform init` | Instancia o template do repo GitOps (embarcado na CLI): `platform/values/<env>.yaml` por ambiente, `apps/`, `argocd/applicationset.yaml`, workflow de validação, CODEOWNERS |
| `platform new-app` | Cria `apps/<nome>/app.yaml` e `env/<env>.yaml` para cada ambiente; com `--from-project` sugere `secrets.keys` lendo o `appsettings.json` |
| `platform validate` | `helm template` nas três camadas + schema do chart + kubeconform (se instalado) + `validate-summary.json`; `--level N` avalia os controles de `conformance/levels.yaml` verificáveis localmente |

Requisitos: `helm` no PATH; `kubeconform` opcional. O chart padrão é o OCI do workflow do tenant; use `--chart <dir>` para um chart local.

Desenvolvimento: `dotnet test cli/Aedis.Cli.slnx`. O template e `conformance/levels.yaml` são recursos embarcados (`EmbeddedResource`), então a CLI e o repo evoluem juntos.

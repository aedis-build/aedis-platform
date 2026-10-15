# apps/

Um diretório por aplicação:

```
apps/<app>/
  app.yaml          # declaração do app (igual em todos os ambientes)
  env/<env>.yaml    # o que muda por ambiente: image.tag/digest, réplicas, rollout, sync
  dast-accept.yaml  # (opcional) aceites de risco do DAST, com validade
```

Regras:

- `name` em `app.yaml` é o nome do diretório.
- Um app existe num ambiente quando `env/<env>.yaml` existe; apagar o arquivo não apaga nada do cluster
  (`preserveResourcesOnDeletion` no ApplicationSet).
- Chaves de segredo são declaradas uma vez em `app.yaml` (`secrets.keys`); só o prefixo do cofre muda por
  ambiente (`platform/values/<env>.yaml`).
- `image.tag` em não-prod é escrita pelo argocd-image-updater; em prod só muda por PR de promoção, que
  também grava `image.digest`.
- `sync.automated: false` congela um app ou marca um cutover.

Crie um app com `aedis platform new-app <nome> --type api --namespace <ns>` (ou copie `apps/_example`).

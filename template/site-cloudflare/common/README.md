# __AEDIS_NAME__

Site Next.js gerado por `aedis platform new-site --target cloudflare`, servido em https://__AEDIS_DOMAIN__ pela
borda da Cloudflare (Workers). Nasce com os componentes do [Aedis UI](https://github.com/aedis-build/aedis-ui)
disponíveis pelo registry `@aedis` e com os cabeçalhos de segurança do Aedis em toda página (CSP sem script inline
fora de nonce ou hash, HSTS, sem framing).

## Rodando

```bash
pnpm install
pnpm dev                      # http://localhost:3000 (sem CSP: o HMR precisa de eval e websocket)
pnpm test:coverage            # vitest + relatório Cobertura (gate 85 % linhas / 70 % branches)
pnpm start                    # wrangler dev em http://localhost:8080, o runtime da borda, depois do build
```

Componentes entram como código seu, pelo shadcn CLI:

```bash
pnpm dlx shadcn@latest add @aedis/button @aedis/input
```

## Deploy

O `.github/workflows/deploy.yml` chama o `deploy-site.yml` do [Aedis Platform](https://github.com/aedis-build/aedis-platform)
em todo push na `main`: build isolado, SBOM, atestação (SLSA Build L3), publicação exatamente dos bytes atestados e
smoke test. O `wrangler.jsonc` declara o domínio próprio (`custom_domain: true`) e desliga `workers.dev` e as URLs
de versão. O domínio e os segredos do Worker são do wrangler; WAF, rate limit, TLS e o resto da zona ficam no
módulo Terraform [`aedis-build/aedis/cloudflare`](https://registry.terraform.io/modules/aedis-build/aedis/cloudflare), declarado em `infra/cloudflare/main.tf` com versão exata.

Uma vez, no GitHub: crie o environment `prd` (só `main`, aprovador obrigatório) com os segredos
`CLOUDFLARE_API_TOKEN` e `CLOUDFLARE_ACCOUNT_ID`. O `.github/workflows/aedis-ci.yml` roda o pipeline único
`aedis-ci` da plataforma com `language: node` (commits, cobertura, SAST, CodeQL, REUSE e DAST contra o site servido
pelo `wrangler dev`).

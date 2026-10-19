# __AEDIS_NAME__

Site Next.js (App Router, saída *standalone*) gerado por `aedis platform new-site`. Nasce com os componentes do
[Aedis UI](https://github.com/aedis-build/aedis-ui) disponíveis pelo registry `@aedis` e com os cabeçalhos de
segurança do `@aedis/security-headers` aplicados em `proxy.ts` (CSP com nonce por requisição, HSTS, sem framing).

## Rodando

```bash
pnpm install
pnpm dev                      # http://localhost:3000 (sem CSP: o HMR precisa de eval e websocket)
pnpm test:coverage            # vitest + relatório Cobertura (gate 85 % linhas / 70 % branches)
pnpm build && pnpm start      # como em produção, em http://localhost:8080
```

Componentes entram como código seu, pelo shadcn CLI:

```bash
pnpm dlx shadcn@latest add @aedis/button @aedis/input
```

## Imagem e deploy

```bash
docker build -t __AEDIS_NAME__ .
```

O `Dockerfile` constrói no `node:22` e roda no `gcr.io/distroless/nodejs22` *nonroot* (sem shell), ambos pinados
por digest. O deploy é declarativo pelo [Aedis Platform](https://github.com/aedis-build/aedis-platform):
`deploy/app.yaml` são os values do chart `aedis-app` com `type: site` (Service, Ingress, HPA, PDB, NetworkPolicy,
sem segredos); copie-o para `apps/__AEDIS_NAME__/app.yaml` no repositório GitOps
(`aedis platform new-app __AEDIS_NAME__ --type site`). O `.github/workflows/aedis-ci.yml` roda o pipeline único `aedis-ci` da plataforma
com `language: node` (commits, cobertura, SAST, CodeQL, REUSE, DAST contra o site servido) e o
`.github/workflows/release.yml`, em tag `vX.Y.Z`, constrói a imagem assinada com SBOM.

## Nível L3

Para o site, L3 significa: DAST verde contra a app servida (`dast-accept.yaml` guarda aceites com validade), WAF
declarado em `ingress.edge.waf` em produção, cabeçalhos de segurança emitidos pela própria app, cobertura ≥ 85/70
sem regressão e testes baseados em propriedades (fast-check) nos parsers; o resto (Scorecard, Best Practices,
imagem assinada) vem do `aedis-ci` e do `release.yml`.

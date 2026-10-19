# syntax=docker/dockerfile:1
# Imagem endurecida: build no node oficial, runtime distroless non-root sem shell nem gerenciador de pacotes.
# Só .next/standalone vai para a imagem final. As proteções de runtime (read-only FS, drop de capabilities,
# seccomp) vêm do chart aedis-app; /app/.next/cache é o único caminho gravável (ver deploy/app.yaml).
FROM node:22-bookworm-slim@sha256:c3de60bf2f9dd0ac6370e6117950ff62d6e339527e7472301c9c78a017978392 AS build
WORKDIR /src
ENV COREPACK_ENABLE_DOWNLOAD_PROMPT=0 NEXT_TELEMETRY_DISABLED=1
RUN corepack enable
COPY package.json pnpm-lock.yaml pnpm-workspace.yaml ./
RUN pnpm install --frozen-lockfile
COPY . .
RUN pnpm build

FROM gcr.io/distroless/nodejs22-debian12:nonroot@sha256:13593b7570658e8477de39e2f4a1dd25db2f836d68a0ba771251572d23bb4f8e
WORKDIR /app
COPY --from=build --chown=nonroot:nonroot /src/.next/standalone ./
ENV NODE_ENV=production PORT=8080 HOSTNAME=0.0.0.0 NEXT_TELEMETRY_DISABLED=1
EXPOSE 8080
# A base :nonroot já roda como 65532; o USER explícito mantém isso se a tag da base mudar.
USER nonroot
CMD ["server.js"]

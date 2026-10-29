import { defineCloudflareConfig } from "@opennextjs/cloudflare";

// Padrões do OpenNext: sem cache incremental (ISR) nem fila de revalidação. Ligue-os aqui (R2, D1, Durable Objects)
// quando o site precisar, com os bindings no wrangler.jsonc.
export default defineCloudflareConfig();

import { createHash } from "node:crypto";

import { securityHeaders } from "@/lib/security-headers";

const INLINE_SCRIPT = /<script(?![^>]*\bsrc=)[^>]*>([\s\S]*?)<\/script>/gi;

/** sha256 de cada script inline, sobre o texto exato que o navegador calcula. */
export function inlineScriptHashes(html: string): string[] {
  return [...html.matchAll(INLINE_SCRIPT)].map(([, body]) => `sha256-${createHash("sha256").update(body, "utf8").digest("base64")}`);
}

/**
 * O arquivo `_headers` da Cloudflare para o site exportado: os cabeçalhos de segurança em todo caminho, com os hashes
 * de todos os scripts inline de todas as páginas numa só política (cada caminho recebe no máximo uma CSP).
 */
export function headersFile(pages: string[]): string {
  const hashes = [...new Set(pages.flatMap(inlineScriptHashes))].sort();
  return [
    "/*",
    ...Object.entries(securityHeaders({ hashes })).map(([name, value]) => `  ${name}: ${value}`),
    // Os assets do build têm hash no nome e nunca mudam: mantêm os cabeçalhos, com cache longo no lugar de no-store.
    "/_next/static/*",
    "  ! Cache-Control",
    "  Cache-Control: public, max-age=31536000, immutable",
    "",
  ].join("\n");
}

import { NextResponse, type NextRequest } from "next/server";

import { securityHeaders } from "@/lib/security-headers";

// Cabeçalhos de segurança do Aedis UI (@aedis/security-headers) em toda resposta de página, com um nonce por
// requisição: a CSP não permite script inline nem eval, e o Next lê o nonce do cabeçalho para os scripts dele.
export function proxy(request: NextRequest) {
  const nonce = Buffer.from(crypto.randomUUID()).toString("base64");
  const headers = securityHeaders({ nonce });
  // O dev server precisa de eval e websocket para o HMR; a CSP estrita vale fora do desenvolvimento.
  if (process.env.NODE_ENV === "development") delete headers["Content-Security-Policy"];

  const requestHeaders = new Headers(request.headers);
  requestHeaders.set("x-nonce", nonce);
  if (headers["Content-Security-Policy"]) requestHeaders.set("Content-Security-Policy", headers["Content-Security-Policy"]);

  const response = NextResponse.next({ request: { headers: requestHeaders } });
  for (const [name, value] of Object.entries(headers)) response.headers.set(name, value);
  return response;
}

export const config = {
  // Páginas e rotas; os assets estáticos têm hash no nome e ficam fora (cache longo, sem nonce).
  matcher: ["/((?!_next/static|_next/image|favicon.ico).*)"],
};

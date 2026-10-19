import type { Metadata } from "next";
import type { ReactNode } from "react";

import "./globals.css";

// A CSP tem nonce por requisição: as páginas são renderizadas por requisição, nunca pré-renderizadas,
// para que os scripts do Next saiam com o nonce da resposta.
export const dynamic = "force-dynamic";

export const metadata: Metadata = {
  title: { default: "__AEDIS_NAME__", template: "%s · __AEDIS_NAME__" },
  description: "Site construído com Aedis UI e publicado pelo Aedis Platform.",
};

export default function RootLayout({ children }: { children: ReactNode }) {
  return (
    <html lang="pt-BR">
      <body className="min-h-screen antialiased">
        <main className="mx-auto max-w-4xl px-4 py-12">{children}</main>
      </body>
    </html>
  );
}

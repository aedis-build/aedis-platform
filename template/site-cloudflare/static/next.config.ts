import type { NextConfig } from "next";

// Export estático servido pelos assets estáticos da Cloudflare. Os cabeçalhos de segurança, com os hashes de todo
// script inline, vão para out/_headers depois do build (scripts/headers.ts).
const config: NextConfig = {
  output: "export",
  images: { unoptimized: true },
  reactStrictMode: true,
  poweredByHeader: false,
};

export default config;

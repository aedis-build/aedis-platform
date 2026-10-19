import type { NextConfig } from "next";

// Saída standalone: o Dockerfile copia só .next/standalone (server.js + o mínimo de node_modules) para a imagem distroless.
const config: NextConfig = {
  output: "standalone",
  reactStrictMode: true,
  poweredByHeader: false,
};

export default config;

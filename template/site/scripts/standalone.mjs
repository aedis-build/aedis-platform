// Completa a saída standalone do Next (que não copia public/ nem .next/static) para que
// `node .next/standalone/server.js` sirva o site inteiro, localmente e na imagem.
import { cpSync, existsSync } from "node:fs";

const out = ".next/standalone";
if (existsSync("public")) cpSync("public", `${out}/public`, { recursive: true });
cpSync(".next/static", `${out}/.next/static`, { recursive: true });

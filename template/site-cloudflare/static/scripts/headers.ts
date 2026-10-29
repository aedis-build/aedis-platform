// `tsx scripts/headers.ts out` depois do `next build`: grava out/_headers com os hashes dos scripts inline de toda página.
import { readdirSync, readFileSync, writeFileSync } from "node:fs";
import { join } from "node:path";

import { headersFile } from "@/lib/headers-file";

const out = process.argv[2] ?? "out";
const pages = readdirSync(out, { withFileTypes: true, recursive: true })
  .filter((e) => e.isFile() && e.name.endsWith(".html"))
  .map((e) => readFileSync(join(e.parentPath, e.name), "utf8"));
writeFileSync(join(out, "_headers"), headersFile(pages));

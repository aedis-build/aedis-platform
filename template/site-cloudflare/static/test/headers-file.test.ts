// @vitest-environment node
import { createHash } from "node:crypto";

import fc from "fast-check";
import { describe, expect, it } from "vitest";

import { headersFile, inlineScriptHashes } from "@/lib/headers-file";

const sha = (s: string) => `sha256-${createHash("sha256").update(s, "utf8").digest("base64")}`;

describe("headersFile", () => {
  it("hash de cada script inline; script com src fica fora", () => {
    const html = '<script>a()</script><script src="/_next/x.js"></script><script type="application/ld+json">{}</script>';
    expect(inlineScriptHashes(html)).toEqual([sha("a()"), sha("{}")]);
  });

  it("uma só CSP em /* com os hashes de todas as páginas, sem repetição, e cache longo nos assets do build", () => {
    const file = headersFile(["<script>a()</script>", "<script>a()</script><script>b()</script>"]);
    expect(file.match(/Content-Security-Policy/g)).toHaveLength(1);
    expect(file).toContain(`'${[sha("a()"), sha("b()")].sort().join("' '")}'`);
    expect(file).toContain("/_next/static/*\n  ! Cache-Control\n  Cache-Control: public, max-age=31536000, immutable");
  });

  it("todo script inline vira um hash na política (propriedade, fast-check)", () => {
    fc.assert(
      fc.property(fc.array(fc.string().filter((s) => !s.includes("<")), { maxLength: 4 }), (bodies) => {
        const file = headersFile([bodies.map((b) => `<script>${b}</script>`).join("")]);
        for (const b of bodies) expect(file).toContain(`'${sha(b)}'`);
      }),
    );
  });
});

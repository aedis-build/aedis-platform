import fc from "fast-check";
import { describe, expect, it } from "vitest";

import { securityHeaders } from "@/lib/security-headers";

const escape = (s: string) => s.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");

describe("securityHeaders", () => {
  it("CSP estrita: só os hashes dos scripts inline, sem eval, sem strict-dynamic, sem framing", () => {
    const headers = securityHeaders({ hashes: ["sha256-abc="] });
    expect(headers["Content-Security-Policy"]).toContain("script-src 'self' 'sha256-abc='");
    expect(headers["Content-Security-Policy"]).not.toContain("unsafe-eval");
    expect(headers["Content-Security-Policy"]).not.toContain("strict-dynamic");
    expect(headers["Content-Security-Policy"]).toContain("frame-ancestors 'none'");
    expect(headers["X-Frame-Options"]).toBe("DENY");
    expect(headers["Strict-Transport-Security"]).toContain("max-age=");
  });

  it("quem pode enquadrar a página e origens de imagem entram só onde foram pedidos", () => {
    const headers = securityHeaders({ hashes: [], frameAncestors: ["https://portal.example.com"], imgSrc: ["https://cdn.example.com"] });
    expect(headers["Content-Security-Policy"]).toContain("frame-ancestors https://portal.example.com");
    expect(headers["Content-Security-Policy"]).toContain("img-src 'self' data: https://cdn.example.com");
    expect(headers["X-Frame-Options"]).toBe("SAMEORIGIN");
  });

  it("origens extras entram só na diretiva pedida (propriedade, fast-check)", () => {
    const origin = fc.webUrl({ validSchemes: ["https"] }).map((u) => new URL(u).origin);
    fc.assert(
      fc.property(fc.array(origin, { maxLength: 3 }), (connectSrc) => {
        const csp = securityHeaders({ hashes: [], connectSrc })["Content-Security-Policy"];
        for (const o of connectSrc) {
          expect(csp).toMatch(new RegExp(`connect-src [^;]*${escape(o)}`));
          expect(csp).not.toMatch(new RegExp(`script-src [^;]*${escape(o)}`));
        }
      }),
    );
  });
});

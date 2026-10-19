import fc from "fast-check";
import { describe, expect, it } from "vitest";

import { securityHeaders } from "@/lib/security-headers";

const escape = (s: string) => s.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");

describe("securityHeaders", () => {
  it("CSP estrita: nonce por requisição, sem inline/eval, sem framing", () => {
    const headers = securityHeaders({ nonce: "abc" });
    expect(headers["Content-Security-Policy"]).toContain("script-src 'self' 'nonce-abc' 'strict-dynamic'");
    expect(headers["Content-Security-Policy"]).not.toContain("unsafe-eval");
    expect(headers["Content-Security-Policy"]).toContain("frame-ancestors 'none'");
    expect(headers["X-Frame-Options"]).toBe("DENY");
    expect(headers["Strict-Transport-Security"]).toContain("max-age=");
  });

  it("quem pode enquadrar a página e origens de imagem entram só onde foram pedidos", () => {
    const headers = securityHeaders({ nonce: "abc", frameAncestors: ["https://portal.example.com"], imgSrc: ["https://cdn.example.com"] });
    expect(headers["Content-Security-Policy"]).toContain("frame-ancestors https://portal.example.com");
    expect(headers["Content-Security-Policy"]).toContain("img-src 'self' data: https://cdn.example.com");
    expect(headers["X-Frame-Options"]).toBe("SAMEORIGIN");
  });

  it("origens extras entram só na diretiva pedida (propriedade, fast-check)", () => {
    const origin = fc.webUrl({ validSchemes: ["https"] }).map((u) => new URL(u).origin);
    fc.assert(
      fc.property(fc.base64String({ minLength: 16 }), fc.array(origin, { maxLength: 3 }), (nonce, connectSrc) => {
        const csp = securityHeaders({ nonce, connectSrc })["Content-Security-Policy"];
        expect(csp).toContain(`'nonce-${nonce}'`);
        for (const o of connectSrc) {
          expect(csp).toMatch(new RegExp(`connect-src [^;]*${escape(o)}`));
          expect(csp).not.toMatch(new RegExp(`script-src [^;]*${escape(o)}`));
        }
      }),
    );
  });
});

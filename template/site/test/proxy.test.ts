// @vitest-environment node
import { NextRequest } from "next/server";
import { describe, expect, it, vi } from "vitest";

import { proxy } from "@/proxy";

describe("proxy", () => {
  it("em desenvolvimento não envia a CSP (o HMR precisa de eval e websocket) e mantém o resto", () => {
    vi.stubEnv("NODE_ENV", "development");
    try {
      const response = proxy(new NextRequest("http://localhost:3000/"));
      expect(response.headers.get("Content-Security-Policy")).toBeNull();
      expect(response.headers.get("X-Frame-Options")).toBe("DENY");
    } finally {
      vi.unstubAllEnvs();
    }
  });

  it("aplica os cabeçalhos de segurança com nonce novo a cada requisição", () => {
    const first = proxy(new NextRequest("http://localhost:8080/"));
    const second = proxy(new NextRequest("http://localhost:8080/"));
    const csp = first.headers.get("Content-Security-Policy")!;
    expect(csp).toMatch(/'nonce-[A-Za-z0-9+/=]+'/);
    expect(csp).not.toBe(second.headers.get("Content-Security-Policy"));
    expect(first.headers.get("X-Content-Type-Options")).toBe("nosniff");
    expect(first.headers.get("Cache-Control")).toBe("no-store");
  });
});

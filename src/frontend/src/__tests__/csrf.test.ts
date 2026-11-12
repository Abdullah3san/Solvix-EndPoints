import { describe, expect, it } from "vitest";
import { newCsrfToken, readCsrfCookie, verifyCsrf, type CsrfInput } from "@/lib/auth/csrf";

const token = newCsrfToken();
const valid: CsrfInput = {
  method: "POST",
  expectedOrigins: ["https://app.solvix.test"],
  origin: "https://app.solvix.test",
  secFetchSite: "same-origin",
  cookieToken: token,
  headerToken: token,
};

describe("CSRF protection", () => {
  it("generates long random tokens", () => {
    const tokens = new Set(Array.from({ length: 50 }, () => newCsrfToken()));
    expect(tokens.size).toBe(50);
    for (const t of tokens) expect(t).toMatch(/^[0-9a-f]{64}$/);
  });

  it("accepts a same-origin request that echoes the cookie", () => {
    expect(verifyCsrf(valid)).toEqual({ ok: true });
    // Older browsers send neither header; the double-submit check still applies.
    expect(verifyCsrf({ ...valid, origin: null, secFetchSite: null })).toEqual({ ok: true });
  });

  it("never blocks safe methods", () => {
    expect(verifyCsrf({ ...valid, method: "GET", origin: "https://evil.test", cookieToken: undefined, headerToken: null }).ok).toBe(true);
  });

  it.each([
    ["a foreign Origin", { origin: "https://evil.test" }, "origin_mismatch"],
    ["a look-alike Origin", { origin: "https://app.solvix.test.evil.test" }, "origin_mismatch"],
    ["a cross-site fetch", { secFetchSite: "cross-site" }, "cross_site_request"],
    ["a same-site (sibling subdomain) fetch", { secFetchSite: "same-site" }, "cross_site_request"],
    ["no header token", { headerToken: null }, "csrf_token_missing"],
    ["no cookie", { cookieToken: undefined }, "csrf_token_missing"],
    ["a short cookie", { cookieToken: "abc", headerToken: "abc" }, "csrf_token_missing"],
    ["a different header token", { headerToken: newCsrfToken() }, "csrf_token_mismatch"],
  ] as const)("rejects %s", (_name, change, reason) => {
    for (const method of ["POST", "PUT", "PATCH", "DELETE"]) {
      expect(verifyCsrf({ ...valid, ...change, method })).toEqual({ ok: false, reason });
    }
  });

  it("reads the cookie under either name", () => {
    expect(readCsrfCookie(`theme=dark; solvix_csrf=${token}; other=1`)).toBe(token);
    expect(readCsrfCookie(`__Host-solvix_csrf=${token}`)).toBe(token);
    expect(readCsrfCookie("solvix_session=abc")).toBeNull();
  });
});

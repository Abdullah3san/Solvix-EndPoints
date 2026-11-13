import { describe, expect, it } from "vitest";
import { cookieNames, isSessionValid, parseSessionClaims, tokenCookieOptions } from "@/lib/auth/session";

function makeToken(payload: Record<string, unknown>): string {
  const encode = (obj: unknown) => Buffer.from(JSON.stringify(obj)).toString("base64url");
  return `${encode({ alg: "HS256", typ: "JWT" })}.${encode(payload)}.signature`;
}

describe("session claims", () => {
  it("parses tenant- and session-bound claims", () => {
    const token = makeToken({ sub: "u1", tid: "t1", tslug: "acme", sid: "s1", exp: 2_000_000_000 });
    expect(parseSessionClaims(token)).toMatchObject({ sub: "u1", tid: "t1", tslug: "acme", sid: "s1" });
  });

  it("rejects malformed tokens and tokens without a tenant or session", () => {
    expect(parseSessionClaims("not.a.jwt.at.all")).toBeNull();
    expect(parseSessionClaims("abc")).toBeNull();
    expect(parseSessionClaims(makeToken({ sub: "u1", sid: "s", exp: 2_000_000_000 }))).toBeNull();
    expect(parseSessionClaims(makeToken({ sub: "u1", tid: "t", exp: 2_000_000_000 }))).toBeNull();
    expect(parseSessionClaims(undefined)).toBeNull();
  });

  it("treats tokens expiring within 30 seconds as invalid", () => {
    const now = 1_700_000_000_000;
    const at = (exp: number) => makeToken({ sub: "u", tid: "t", sid: "s", exp });
    expect(isSessionValid(at(now / 1000 + 60), now)).toBe(true);
    expect(isSessionValid(at(now / 1000 + 10), now)).toBe(false);
    expect(isSessionValid(at(now / 1000 - 1), now)).toBe(false);
  });
});

describe("session cookies", () => {
  it("are httpOnly, SameSite=Lax and site-wide", () => {
    const options = tokenCookieOptions("2030-01-01T00:00:00Z", true);
    expect(options).toMatchObject({ httpOnly: true, sameSite: "lax", secure: true, path: "/" });
    expect(options.expires.toISOString()).toBe("2030-01-01T00:00:00.000Z");
  });

  it("use the __Host- prefix whenever they are Secure", () => {
    expect(cookieNames(true)).toEqual({ access: "__Host-solvix_session", refresh: "__Host-solvix_refresh", csrf: "__Host-solvix_csrf" });
    expect(cookieNames(false)).toEqual({ access: "solvix_session", refresh: "solvix_refresh", csrf: "solvix_csrf" });
  });
});

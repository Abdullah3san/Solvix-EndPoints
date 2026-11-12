import { describe, expect, it } from "vitest";
import { isSessionValid, parseSessionClaims } from "@/lib/auth/session";

function makeToken(payload: Record<string, unknown>): string {
  const encode = (obj: unknown) => Buffer.from(JSON.stringify(obj)).toString("base64url");
  return `${encode({ alg: "HS256", typ: "JWT" })}.${encode(payload)}.signature`;
}

describe("session claims", () => {
  it("parses tenant-bound claims", () => {
    const token = makeToken({ sub: "u1", tid: "t1", tslug: "acme", role: "Owner", exp: 2_000_000_000 });
    const claims = parseSessionClaims(token);
    expect(claims).toMatchObject({ sub: "u1", tid: "t1", tslug: "acme", role: "Owner" });
  });

  it("rejects malformed tokens and tokens without a tenant", () => {
    expect(parseSessionClaims("not.a.jwt.at.all")).toBeNull();
    expect(parseSessionClaims("abc")).toBeNull();
    expect(parseSessionClaims(makeToken({ sub: "u1", exp: 2_000_000_000 }))).toBeNull();
    expect(parseSessionClaims(undefined)).toBeNull();
  });

  it("treats tokens expiring within 30 seconds as invalid", () => {
    const now = 1_700_000_000_000;
    expect(isSessionValid(makeToken({ sub: "u", tid: "t", exp: now / 1000 + 60 }), now)).toBe(true);
    expect(isSessionValid(makeToken({ sub: "u", tid: "t", exp: now / 1000 + 10 }), now)).toBe(false);
    expect(isSessionValid(makeToken({ sub: "u", tid: "t", exp: now / 1000 - 1 }), now)).toBe(false);
  });
});

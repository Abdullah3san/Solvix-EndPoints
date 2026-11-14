/**
 * Session cookie helpers shared by the proxy, route handlers and server
 * components. The access token lives in an httpOnly cookie and never reaches
 * browser JavaScript; the BFF (/api/proxy) attaches it to upstream calls.
 */
export const SESSION_COOKIE = "solvix_session";

export interface SessionClaims {
  sub: string;
  email?: string;
  name?: string;
  tid: string;
  tslug?: string;
  role?: string;
  exp: number;
}

function base64UrlDecode(input: string): string {
  const normalized = input.replace(/-/g, "+").replace(/_/g, "/");
  const padded = normalized + "=".repeat((4 - (normalized.length % 4)) % 4);
  if (typeof atob === "function") {
    const binary = atob(padded);
    const bytes = Uint8Array.from(binary, (c) => c.charCodeAt(0));
    return new TextDecoder().decode(bytes);
  }
  return Buffer.from(padded, "base64").toString("utf8");
}

/**
 * Reads the claims of a JWT WITHOUT verifying the signature. This is only used
 * for UX decisions (redirect to login when expired); authorization is always
 * enforced by the API, which validates the signature.
 */
export function parseSessionClaims(token: string | undefined | null): SessionClaims | null {
  if (!token) return null;
  const parts = token.split(".");
  if (parts.length !== 3) return null;
  try {
    const payload = JSON.parse(base64UrlDecode(parts[1])) as Partial<SessionClaims>;
    if (typeof payload.sub !== "string" || typeof payload.tid !== "string" || typeof payload.exp !== "number") {
      return null;
    }
    return payload as SessionClaims;
  } catch {
    return null;
  }
}

export function isSessionValid(token: string | undefined | null, now: number = Date.now()): boolean {
  const claims = parseSessionClaims(token);
  if (!claims) return false;
  // 30 second safety margin so we never send a token that is about to expire.
  return claims.exp * 1000 > now + 30_000;
}

export function sessionCookieOptions(expiresAtUtc: string, secure: boolean) {
  return {
    httpOnly: true,
    sameSite: "lax" as const,
    secure,
    path: "/",
    expires: new Date(expiresAtUtc),
  };
}

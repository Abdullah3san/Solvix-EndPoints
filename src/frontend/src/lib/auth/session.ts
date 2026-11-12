/**
 * Session cookie helpers shared by the proxy, route handlers and server
 * components.
 *
 * Tokens never reach browser JavaScript: the access token and the refresh
 * token live in httpOnly cookies set by the BFF route handlers, and the BFF
 * (/api/proxy) attaches the access token to upstream calls. Nothing is ever
 * written to localStorage or sessionStorage.
 *
 * When cookies are Secure they also carry the `__Host-` prefix, which makes
 * the browser refuse them unless they are Secure, host-only and Path=/, so a
 * sibling subdomain cannot plant or overwrite them.
 */
const BASE_NAMES = { access: "solvix_session", refresh: "solvix_refresh", csrf: "solvix_csrf" } as const;

export interface CookieNames {
  access: string;
  refresh: string;
  csrf: string;
}

export function cookieNames(secure: boolean): CookieNames {
  const prefix = secure ? "__Host-" : "";
  return { access: prefix + BASE_NAMES.access, refresh: prefix + BASE_NAMES.refresh, csrf: prefix + BASE_NAMES.csrf };
}

export interface SessionClaims {
  sub: string;
  email?: string;
  name?: string;
  tid: string;
  tslug?: string;
  sid: string;
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
 * for UX decisions (refresh or redirect when expired); authorization is always
 * enforced by the API, which validates the signature and the session.
 */
export function parseSessionClaims(token: string | undefined | null): SessionClaims | null {
  if (!token) return null;
  const parts = token.split(".");
  if (parts.length !== 3) return null;
  try {
    const payload = JSON.parse(base64UrlDecode(parts[1])) as Partial<SessionClaims>;
    if (
      typeof payload.sub !== "string" ||
      typeof payload.tid !== "string" ||
      typeof payload.sid !== "string" ||
      typeof payload.exp !== "number"
    ) {
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

/** Options for the two token cookies: never readable by scripts, never sent cross-site with unsafe methods. */
export function tokenCookieOptions(expiresAtUtc: string, secure: boolean) {
  return {
    httpOnly: true,
    sameSite: "lax" as const,
    secure,
    path: "/",
    expires: new Date(expiresAtUtc),
  };
}

export function expiredCookieOptions(secure: boolean, httpOnly = true) {
  return { httpOnly, sameSite: "lax" as const, secure, path: "/", maxAge: 0 };
}

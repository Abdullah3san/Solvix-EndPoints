/**
 * Cross-site request forgery protection for the BFF. The API itself is called
 * with a bearer token and no cookies, so only the cookie-authenticated routes
 * of this site need it. Three independent checks, all of which must pass for
 * every state-changing request:
 *
 *  1. Origin: when the browser sends an Origin header it must be this site.
 *  2. Fetch metadata: a request the browser labels cross-site is refused.
 *  3. Double submit: the request must repeat the random value of the CSRF
 *     cookie in the X-CSRF-Token header. Another site can neither read the
 *     cookie nor set that header on a cross-site request.
 *
 * The session cookies are additionally SameSite=Lax.
 */
export const CSRF_HEADER = "x-csrf-token";

const SAFE_METHODS = new Set(["GET", "HEAD", "OPTIONS"]);

export function newCsrfToken(): string {
  const bytes = new Uint8Array(32);
  crypto.getRandomValues(bytes);
  return Array.from(bytes, (b) => b.toString(16).padStart(2, "0")).join("");
}

function constantTimeEquals(a: string, b: string): boolean {
  if (a.length !== b.length) return false;
  let diff = 0;
  for (let i = 0; i < a.length; i++) diff |= a.charCodeAt(i) ^ b.charCodeAt(i);
  return diff === 0;
}

export interface CsrfInput {
  method: string;
  /** Origin this site is served from, as the browser sees it. */
  expectedOrigins: string[];
  origin: string | null;
  secFetchSite: string | null;
  cookieToken: string | undefined;
  headerToken: string | null;
}

export type CsrfResult = { ok: true } | { ok: false; reason: string };

export function verifyCsrf(input: CsrfInput): CsrfResult {
  if (SAFE_METHODS.has(input.method.toUpperCase())) return { ok: true };

  if (input.origin !== null && !input.expectedOrigins.includes(input.origin)) {
    return { ok: false, reason: "origin_mismatch" };
  }
  if (input.secFetchSite !== null && input.secFetchSite !== "same-origin" && input.secFetchSite !== "none") {
    return { ok: false, reason: "cross_site_request" };
  }
  if (!input.cookieToken || !input.headerToken || input.cookieToken.length < 32) {
    return { ok: false, reason: "csrf_token_missing" };
  }
  if (!constantTimeEquals(input.cookieToken, input.headerToken)) {
    return { ok: false, reason: "csrf_token_mismatch" };
  }
  return { ok: true };
}

/** Browser side: reads the CSRF cookie so it can be echoed in a header. The cookie holds no secret about the session. */
export function readCsrfCookie(cookieString: string): string | null {
  for (const part of cookieString.split(";")) {
    const [name, ...rest] = part.trim().split("=");
    if (name === "solvix_csrf" || name === "__Host-solvix_csrf") return decodeURIComponent(rest.join("="));
  }
  return null;
}

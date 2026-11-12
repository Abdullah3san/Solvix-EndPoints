import "server-only";
import { NextResponse, type NextRequest } from "next/server";
import { serverConfig } from "@/lib/config";
import type { AuthResponse } from "@/lib/api/types";
import { CSRF_HEADER, verifyCsrf } from "./csrf";
import { cookieNames, expiredCookieOptions, tokenCookieOptions } from "./session";

export const names = cookieNames(serverConfig.cookieSecure);

export function upstreamUrl(path: string) {
  return `${serverConfig.apiBaseUrl}/api/${serverConfig.apiVersion}/${path.replace(/^\/+/, "")}`;
}

/** Headers that tell the API who the real client is. The API only honours them from this (trusted) proxy. */
export function clientHeaders(request: NextRequest): Record<string, string> {
  const headers: Record<string, string> = {};
  const forwardedFor = request.headers.get("x-forwarded-for");
  if (forwardedFor) headers["x-forwarded-for"] = forwardedFor;
  const userAgent = request.headers.get("user-agent");
  if (userAgent) headers["user-agent"] = userAgent;
  const correlation = request.headers.get("x-correlation-id");
  if (correlation) headers["x-correlation-id"] = correlation;
  return headers;
}

export function problem(status: number, code: string, title: string, detail?: string) {
  return NextResponse.json(
    { title, status, code, detail },
    { status, headers: { "content-type": "application/problem+json", "cache-control": "no-store" } },
  );
}

/** Returns an error response when the request fails the CSRF checks, otherwise null. */
export function rejectCsrf(request: NextRequest): NextResponse | null {
  const result = verifyCsrf({
    method: request.method,
    expectedOrigins: [serverConfig.webOrigin, request.nextUrl.origin].filter((o): o is string => !!o),
    origin: request.headers.get("origin"),
    secFetchSite: request.headers.get("sec-fetch-site"),
    cookieToken: request.cookies.get(names.csrf)?.value,
    headerToken: request.headers.get(CSRF_HEADER),
  });
  return result.ok ? null : problem(403, "csrf_rejected", "Forbidden", "This request could not be verified. Reload the page and try again.");
}

/** Stores both tokens in httpOnly cookies. They are never part of a response body sent to the browser. */
export function setSessionCookies(response: NextResponse, auth: AuthResponse) {
  response.cookies.set(names.access, auth.accessToken, tokenCookieOptions(auth.accessTokenExpiresAtUtc, serverConfig.cookieSecure));
  response.cookies.set(names.refresh, auth.refreshToken, tokenCookieOptions(auth.refreshTokenExpiresAtUtc, serverConfig.cookieSecure));
}

export function clearSessionCookies(response: NextResponse) {
  response.cookies.set(names.access, "", expiredCookieOptions(serverConfig.cookieSecure));
  response.cookies.set(names.refresh, "", expiredCookieOptions(serverConfig.cookieSecure));
}

/** What the browser is allowed to know about a new session: who and where, never the tokens. */
export function publicSession(auth: AuthResponse) {
  return { user: auth.user, tenant: auth.tenant, sessionId: auth.sessionId };
}

export async function passThrough(upstream: Response) {
  const body = upstream.status === 204 ? null : await upstream.text();
  return new NextResponse(body, {
    status: upstream.status,
    headers: {
      ...(body !== null ? { "content-type": upstream.headers.get("content-type") ?? "application/problem+json" } : {}),
      ...(upstream.headers.get("retry-after") ? { "retry-after": upstream.headers.get("retry-after")! } : {}),
      "cache-control": "no-store",
    },
  });
}

export async function postUpstream(request: NextRequest, path: string, body: unknown, accessToken?: string) {
  return fetch(upstreamUrl(path), {
    method: "POST",
    headers: {
      "content-type": "application/json",
      accept: "application/json",
      ...clientHeaders(request),
      ...(accessToken ? { authorization: `Bearer ${accessToken}` } : {}),
    },
    body: JSON.stringify(body ?? {}),
    cache: "no-store",
  });
}

// One refresh per refresh token at a time within this process. Parallel requests from the
// same browser share the result instead of racing each other through token rotation.
const inFlight = new Map<string, Promise<AuthResponse | null>>();

export function refreshSession(refreshToken: string, headers: Record<string, string>): Promise<AuthResponse | null> {
  const existing = inFlight.get(refreshToken);
  if (existing) return existing;

  const attempt = (async () => {
    try {
      const response = await fetch(upstreamUrl("auth/refresh"), {
        method: "POST",
        headers: { "content-type": "application/json", accept: "application/json", ...headers },
        body: JSON.stringify({ refreshToken }),
        cache: "no-store",
      });
      return response.ok ? ((await response.json()) as AuthResponse) : null;
    } catch {
      return null;
    }
  })();

  inFlight.set(refreshToken, attempt);
  // Keep the result briefly so a request that was already on its way with the old cookie reuses it.
  void attempt.finally(() => setTimeout(() => inFlight.delete(refreshToken), 5_000));
  return attempt;
}

import { NextRequest, NextResponse } from "next/server";
import { serverConfig } from "@/lib/config";
import type { AuthResponse } from "@/lib/api/types";
import { clearSessionCookies, clientHeaders, names, problem, refreshSession, rejectCsrf, setSessionCookies } from "@/lib/auth/bff";
import { isSessionValid } from "@/lib/auth/session";

/**
 * Backend-for-frontend proxy: /api/proxy/v1/... -> {API}/api/v1/...
 *
 * Attaches the access token from the httpOnly cookie as a bearer token and
 * streams the response back unchanged (status, body, problem+json). When the
 * access token has expired it is renewed with the refresh cookie first, so the
 * browser never handles a token. Only the API version prefix is allowed, so
 * the proxy cannot be used to reach other upstream paths, and the sign-in
 * endpoints (whose responses contain tokens) are not reachable through it.
 */
const FORWARDED_REQUEST_HEADERS = ["content-type", "accept", "x-correlation-id"];
const FORWARDED_RESPONSE_HEADERS = [
  "content-type",
  "content-disposition",
  "content-security-policy",
  "x-content-type-options",
  "x-correlation-id",
  "api-supported-versions",
  "location",
  "retry-after",
];

// Responses of these endpoints carry tokens; they are only ever called by the dedicated /api/auth routes.
const TOKEN_ENDPOINTS = new Set(["auth/login", "auth/refresh", "auth/mfa/verify", "auth/switch-tenant", "auth/logout", "tenant/register"]);

async function forward(request: NextRequest, context: { params: Promise<{ path: string[] }> }) {
  const { path } = await context.params;
  if (!path?.length || path[0] !== serverConfig.apiVersion || TOKEN_ENDPOINTS.has(path.slice(1).join("/").toLowerCase())) {
    return problem(404, "not_found", "Not found");
  }

  const rejected = rejectCsrf(request);
  if (rejected) return rejected;

  let token = request.cookies.get(names.access)?.value;
  let renewed: AuthResponse | null = null;
  if (!isSessionValid(token)) {
    const refreshToken = request.cookies.get(names.refresh)?.value;
    renewed = refreshToken ? await refreshSession(refreshToken, clientHeaders(request)) : null;
    if (!renewed) {
      const expired = problem(401, "unauthorized", "Unauthorized", "Your session has expired.");
      clearSessionCookies(expired);
      return expired;
    }
    token = renewed.accessToken;
  }

  const upstream = new URL(`${serverConfig.apiBaseUrl}/api/${path.map(encodeURIComponent).join("/")}`);
  upstream.search = request.nextUrl.search;

  const headers = new Headers(clientHeaders(request));
  for (const name of FORWARDED_REQUEST_HEADERS) {
    const value = request.headers.get(name);
    if (value) headers.set(name, value);
  }
  headers.set("authorization", `Bearer ${token}`);

  const hasBody = request.method !== "GET" && request.method !== "HEAD";
  // Binary uploads (software packages, up to gigabytes) are streamed through instead of buffered in memory.
  const streamBody = hasBody && request.headers.get("content-type")?.toLowerCase().startsWith("application/octet-stream") && request.body !== null;
  const response = await fetch(upstream, {
    method: request.method,
    headers,
    body: streamBody ? request.body : hasBody ? await request.arrayBuffer() : undefined,
    cache: "no-store",
    redirect: "manual",
    ...(streamBody ? { duplex: "half" } : {}),
  } as RequestInit);

  const responseHeaders = new Headers();
  for (const name of FORWARDED_RESPONSE_HEADERS) {
    const value = response.headers.get(name);
    if (value) responseHeaders.set(name, value);
  }
  responseHeaders.set("cache-control", "no-store");

  const result =
    response.status === 204
      ? new NextResponse(null, { status: 204, headers: responseHeaders })
      : new NextResponse(response.body, { status: response.status, headers: responseHeaders });

  if (renewed) setSessionCookies(result, renewed);
  return result;
}

export const GET = forward;
export const POST = forward;
export const PUT = forward;
export const PATCH = forward;
export const DELETE = forward;

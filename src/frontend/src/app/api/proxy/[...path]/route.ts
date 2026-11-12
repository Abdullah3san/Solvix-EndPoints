import { NextRequest, NextResponse } from "next/server";
import { cookies } from "next/headers";
import { serverConfig } from "@/lib/config";
import { SESSION_COOKIE, isSessionValid } from "@/lib/auth/session";

/**
 * Backend-for-frontend proxy: /api/proxy/v1/... -> {API}/api/v1/...
 * Attaches the session cookie as a bearer token and streams the response
 * back unchanged (status, body, problem+json). Only the API version prefix
 * is allowed, so the proxy cannot be used to reach other upstream paths.
 */
const FORWARDED_REQUEST_HEADERS = ["content-type", "accept", "x-correlation-id"];
const FORWARDED_RESPONSE_HEADERS = ["content-type", "content-disposition", "content-security-policy", "x-content-type-options", "x-correlation-id", "api-supported-versions", "location"];

async function forward(request: NextRequest, context: { params: Promise<{ path: string[] }> }) {
  const { path } = await context.params;
  if (!path?.length || path[0] !== serverConfig.apiVersion) {
    return NextResponse.json({ title: "Not found", status: 404, code: "not_found" }, { status: 404 });
  }

  const store = await cookies();
  const token = store.get(SESSION_COOKIE)?.value;
  if (!isSessionValid(token)) {
    return NextResponse.json(
      { title: "Unauthorized", status: 401, code: "unauthorized", detail: "Your session has expired." },
      { status: 401, headers: { "content-type": "application/problem+json" } },
    );
  }

  const upstream = new URL(`${serverConfig.apiBaseUrl}/api/${path.map(encodeURIComponent).join("/")}`);
  upstream.search = request.nextUrl.search;

  const headers = new Headers();
  for (const name of FORWARDED_REQUEST_HEADERS) {
    const value = request.headers.get(name);
    if (value) headers.set(name, value);
  }
  headers.set("authorization", `Bearer ${token}`);

  const hasBody = request.method !== "GET" && request.method !== "HEAD";
  const response = await fetch(upstream, {
    method: request.method,
    headers,
    body: hasBody ? await request.arrayBuffer() : undefined,
    cache: "no-store",
    redirect: "manual",
  });

  const responseHeaders = new Headers();
  for (const name of FORWARDED_RESPONSE_HEADERS) {
    const value = response.headers.get(name);
    if (value) responseHeaders.set(name, value);
  }
  responseHeaders.set("cache-control", "no-store");

  if (response.status === 204) {
    return new NextResponse(null, { status: 204, headers: responseHeaders });
  }

  return new NextResponse(response.body, { status: response.status, headers: responseHeaders });
}

export const GET = forward;
export const POST = forward;
export const PUT = forward;
export const PATCH = forward;
export const DELETE = forward;

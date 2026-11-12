import { NextRequest, NextResponse } from "next/server";
import { cookies } from "next/headers";
import { serverConfig } from "@/lib/config";
import { SESSION_COOKIE, isSessionValid } from "@/lib/auth/session";
import { isAllowedRealtimePath } from "@/lib/realtime";

/**
 * BFF for SignalR: /api/realtime/hubs/monitoring/... -> {API}/hubs/monitoring/...
 *
 * The session token stays in its httpOnly cookie and is attached here, so the
 * browser never sees it. Route handlers cannot upgrade to WebSockets, so the
 * browser uses Server-Sent Events (long polling as fallback); the response body
 * is streamed through unchanged. Only the monitoring hub can be reached.
 */
export const dynamic = "force-dynamic";

const FORWARDED_REQUEST_HEADERS = ["content-type", "accept", "x-requested-with", "x-signalr-user-agent"];
const FORWARDED_RESPONSE_HEADERS = ["content-type"];

async function forward(request: NextRequest, context: { params: Promise<{ path: string[] }> }) {
  const { path } = await context.params;
  if (!isAllowedRealtimePath(path)) {
    return NextResponse.json({ title: "Not found", status: 404, code: "not_found" }, { status: 404 });
  }

  const store = await cookies();
  const token = store.get(SESSION_COOKIE)?.value;
  if (!isSessionValid(token)) {
    return NextResponse.json({ title: "Unauthorized", status: 401, code: "unauthorized" }, { status: 401 });
  }

  const upstream = new URL(`${serverConfig.apiBaseUrl}/${path.map(encodeURIComponent).join("/")}`);
  upstream.search = request.nextUrl.search;

  const headers = new Headers();
  for (const name of FORWARDED_REQUEST_HEADERS) {
    const value = request.headers.get(name);
    if (value) headers.set(name, value);
  }
  headers.set("authorization", `Bearer ${token}`);

  const hasBody = request.method !== "GET" && request.method !== "HEAD";
  let response: Response;
  try {
    response = await fetch(upstream, {
      method: request.method,
      headers,
      body: hasBody ? await request.arrayBuffer() : undefined,
      cache: "no-store",
      redirect: "manual",
      // Ends the upstream SSE/long-poll request when the browser goes away.
      signal: request.signal,
    });
  } catch {
    return NextResponse.json({ title: "Service unavailable", status: 503, code: "realtime_unavailable" }, { status: 503 });
  }

  const responseHeaders = new Headers();
  for (const name of FORWARDED_RESPONSE_HEADERS) {
    const value = response.headers.get(name);
    if (value) responseHeaders.set(name, value);
  }
  responseHeaders.set("cache-control", "no-cache, no-store");
  responseHeaders.set("x-accel-buffering", "no"); // do not let a reverse proxy buffer the event stream

  if (response.status === 204) {
    return new NextResponse(null, { status: 204, headers: responseHeaders });
  }

  return new NextResponse(response.body, { status: response.status, headers: responseHeaders });
}

export const GET = forward;
export const POST = forward;
export const DELETE = forward;

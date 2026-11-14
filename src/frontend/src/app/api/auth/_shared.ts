import { NextResponse } from "next/server";
import { serverConfig } from "@/lib/config";
import { SESSION_COOKIE, sessionCookieOptions } from "@/lib/auth/session";
import type { AuthResponse } from "@/lib/api/types";

/** Stores the access token in the httpOnly session cookie and returns only non-secret session info. */
export function respondWithSession(auth: AuthResponse, status = 200) {
  const { accessToken, ...publicSession } = auth;
  const response = NextResponse.json(publicSession, { status });
  response.cookies.set(SESSION_COOKIE, accessToken, sessionCookieOptions(auth.expiresAtUtc, serverConfig.cookieSecure));
  return response;
}

export async function passThroughError(upstream: Response) {
  const body = await upstream.text();
  return new NextResponse(body, {
    status: upstream.status,
    headers: { "content-type": upstream.headers.get("content-type") ?? "application/problem+json" },
  });
}

export function upstreamUrl(path: string) {
  return `${serverConfig.apiBaseUrl}/api/${serverConfig.apiVersion}/${path.replace(/^\/+/, "")}`;
}

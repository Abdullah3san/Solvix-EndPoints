import { NextRequest, NextResponse } from "next/server";
import type { RegisterTenantResponse } from "@/lib/api/types";
import { serverConfig } from "@/lib/config";
import { SESSION_COOKIE, sessionCookieOptions } from "@/lib/auth/session";
import { passThroughError, upstreamUrl } from "../_shared";

export async function POST(request: NextRequest) {
  const payload = await request.json();
  const upstream = await fetch(upstreamUrl("tenant/register"), {
    method: "POST",
    headers: { "content-type": "application/json", accept: "application/json" },
    body: JSON.stringify(payload),
    cache: "no-store",
  });
  if (!upstream.ok) return passThroughError(upstream);

  const result = (await upstream.json()) as RegisterTenantResponse;
  const { accessToken, ...publicAuth } = result.auth;
  const response = NextResponse.json({ ...result, auth: publicAuth }, { status: 201 });
  response.cookies.set(SESSION_COOKIE, accessToken, sessionCookieOptions(result.auth.expiresAtUtc, serverConfig.cookieSecure));
  return response;
}

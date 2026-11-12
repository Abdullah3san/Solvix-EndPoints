import { NextRequest, NextResponse } from "next/server";
import { cookies } from "next/headers";
import type { AuthResponse } from "@/lib/api/types";
import { SESSION_COOKIE, isSessionValid } from "@/lib/auth/session";
import { passThroughError, respondWithSession, upstreamUrl } from "../_shared";

export async function POST(request: NextRequest) {
  const store = await cookies();
  const token = store.get(SESSION_COOKIE)?.value;
  if (!isSessionValid(token)) {
    return NextResponse.json({ title: "Unauthorized", status: 401, code: "unauthorized" }, { status: 401 });
  }
  const payload = await request.json();
  const upstream = await fetch(upstreamUrl("auth/switch-tenant"), {
    method: "POST",
    headers: { "content-type": "application/json", accept: "application/json", authorization: `Bearer ${token}` },
    body: JSON.stringify(payload),
    cache: "no-store",
  });
  if (!upstream.ok) return passThroughError(upstream);
  return respondWithSession((await upstream.json()) as AuthResponse);
}

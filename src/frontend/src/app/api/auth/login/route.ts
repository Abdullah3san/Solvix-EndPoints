import { NextRequest } from "next/server";
import type { AuthResponse } from "@/lib/api/types";
import { passThroughError, respondWithSession, upstreamUrl } from "../_shared";

export async function POST(request: NextRequest) {
  const payload = await request.json();
  const upstream = await fetch(upstreamUrl("auth/login"), {
    method: "POST",
    headers: { "content-type": "application/json", accept: "application/json" },
    body: JSON.stringify(payload),
    cache: "no-store",
  });
  if (!upstream.ok) return passThroughError(upstream);
  return respondWithSession((await upstream.json()) as AuthResponse);
}

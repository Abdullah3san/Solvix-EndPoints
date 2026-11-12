import { NextRequest, NextResponse } from "next/server";
import type { AuthResponse } from "@/lib/api/types";
import { passThrough, postUpstream, publicSession, rejectCsrf, setSessionCookies } from "@/lib/auth/bff";

/** Second step of sign-in: authenticator code or recovery code. */
export async function POST(request: NextRequest) {
  const rejected = rejectCsrf(request);
  if (rejected) return rejected;

  const upstream = await postUpstream(request, "auth/mfa/verify", await request.json());
  if (!upstream.ok) return passThrough(upstream);

  const auth = (await upstream.json()) as AuthResponse;
  const response = NextResponse.json({ session: publicSession(auth) }, { headers: { "cache-control": "no-store" } });
  setSessionCookies(response, auth);
  return response;
}

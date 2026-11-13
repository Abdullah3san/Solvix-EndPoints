import { NextRequest, NextResponse } from "next/server";
import type { LoginResponse } from "@/lib/api/types";
import { passThrough, postUpstream, publicSession, rejectCsrf, setSessionCookies } from "@/lib/auth/bff";

/** Password sign-in. A completed sign-in sets the session cookies; an MFA challenge is passed on to the form. */
export async function POST(request: NextRequest) {
  const rejected = rejectCsrf(request);
  if (rejected) return rejected;

  const upstream = await postUpstream(request, "auth/login", await request.json());
  if (!upstream.ok) return passThrough(upstream);

  const login = (await upstream.json()) as LoginResponse;
  if (login.mfaRequired || !login.session) {
    // The challenge token only allows finishing this one sign-in with a valid code; it is not a session.
    return NextResponse.json({ mfaRequired: true, challenge: login.challenge }, { headers: { "cache-control": "no-store" } });
  }

  const response = NextResponse.json({ mfaRequired: false, session: publicSession(login.session) }, { headers: { "cache-control": "no-store" } });
  setSessionCookies(response, login.session);
  return response;
}

import { NextRequest, NextResponse } from "next/server";
import { clearSessionCookies, names, postUpstream, rejectCsrf } from "@/lib/auth/bff";
import { isSessionValid } from "@/lib/auth/session";

/** Ends the session on the server (so the tokens are dead everywhere), then clears the cookies. */
export async function POST(request: NextRequest) {
  const rejected = rejectCsrf(request);
  if (rejected) return rejected;

  const access = request.cookies.get(names.access)?.value;
  const refreshToken = request.cookies.get(names.refresh)?.value;
  try {
    await postUpstream(request, "auth/logout", { refreshToken: refreshToken ?? null }, isSessionValid(access) ? access : undefined);
  } catch {
    // Cookies are cleared regardless; the session then simply expires on the server.
  }

  const response = NextResponse.json({ ok: true }, { headers: { "cache-control": "no-store" } });
  clearSessionCookies(response);
  return response;
}

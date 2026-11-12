import { NextRequest, NextResponse } from "next/server";
import { clearSessionCookies, names, upstreamUrl } from "@/lib/auth/bff";

/**
 * Where pages send the browser when the API no longer accepts its session
 * (signed out elsewhere, deactivated, removed, workspace suspended) while the
 * access token itself has not expired yet. Asks the API first, so this GET
 * cannot be abused cross-site to sign a working session out: cookies are only
 * cleared when the API itself rejects them.
 */
export async function GET(request: NextRequest) {
  const access = request.cookies.get(names.access)?.value;
  let rejected = true;
  if (access) {
    try {
      const response = await fetch(upstreamUrl("auth/me"), { headers: { authorization: `Bearer ${access}` }, cache: "no-store" });
      rejected = response.status === 401 || response.status === 403;
    } catch {
      rejected = false;
    }
  }

  if (!rejected) return NextResponse.redirect(new URL("/dashboard", request.url));

  const response = NextResponse.redirect(new URL("/login?reason=expired", request.url));
  clearSessionCookies(response);
  return response;
}

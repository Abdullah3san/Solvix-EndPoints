import { NextRequest, NextResponse } from "next/server";
import { SESSION_COOKIE } from "@/lib/auth/session";

/**
 * Landing point for "the API rejected this session". Clears the cookie before
 * sending the user to the sign-in page; without that the route gate would see a
 * not-yet-expired cookie and bounce straight back into the app (a redirect loop).
 */
export function GET(request: NextRequest) {
  const login = new URL("/login", request.url);
  login.searchParams.set("reason", "expired");
  const response = NextResponse.redirect(login);
  response.cookies.set(SESSION_COOKIE, "", { httpOnly: true, sameSite: "lax", path: "/", maxAge: 0 });
  return response;
}

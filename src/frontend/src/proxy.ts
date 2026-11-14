import { NextResponse, type NextRequest } from "next/server";
import { SESSION_COOKIE, isSessionValid } from "@/lib/auth/session";

const PUBLIC_PATHS = new Set(["/login", "/register"]);

/**
 * Route gate: unauthenticated users are sent to /login, authenticated users
 * are kept out of the auth pages. API routes handle their own auth.
 */
export function proxy(request: NextRequest) {
  const { pathname, search } = request.nextUrl;
  const authenticated = isSessionValid(request.cookies.get(SESSION_COOKIE)?.value);

  if (PUBLIC_PATHS.has(pathname)) {
    if (authenticated) {
      return NextResponse.redirect(new URL("/dashboard", request.url));
    }
    return NextResponse.next();
  }

  if (!authenticated) {
    const login = new URL("/login", request.url);
    if (pathname !== "/") {
      login.searchParams.set("next", pathname + search);
    }
    if (request.cookies.has(SESSION_COOKIE)) {
      login.searchParams.set("reason", "expired");
    }
    return NextResponse.redirect(login);
  }

  if (pathname === "/") {
    return NextResponse.redirect(new URL("/dashboard", request.url));
  }

  return NextResponse.next();
}

export const config = {
  // Everything except Next internals, static assets and the API routes.
  matcher: ["/((?!api|_next/static|_next/image|favicon.ico|icon.svg|robots.txt).*)"],
};

import { NextResponse, type NextRequest } from "next/server";
import { serverConfig } from "@/lib/config";
import type { AuthResponse } from "@/lib/api/types";
import { newCsrfToken } from "@/lib/auth/csrf";
import { cookieNames, expiredCookieOptions, isSessionValid, tokenCookieOptions } from "@/lib/auth/session";

const names = cookieNames(serverConfig.cookieSecure);

/** Pages for signed-out visitors. Signed-in users are sent to the app instead. */
const AUTH_PAGES = new Set(["/login", "/register", "/forgot-password"]);

/** Pages reached from e-mail links: usable with or without a session. */
const LINK_PAGES = new Set(["/verify-email", "/reset-password", "/accept-invitation"]);

async function refresh(request: NextRequest, refreshToken: string): Promise<AuthResponse | null> {
  try {
    const forwardedFor = request.headers.get("x-forwarded-for");
    const response = await fetch(`${serverConfig.apiBaseUrl}/api/${serverConfig.apiVersion}/auth/refresh`, {
      method: "POST",
      headers: {
        "content-type": "application/json",
        accept: "application/json",
        ...(forwardedFor ? { "x-forwarded-for": forwardedFor } : {}),
        ...(request.headers.get("user-agent") ? { "user-agent": request.headers.get("user-agent")! } : {}),
      },
      body: JSON.stringify({ refreshToken }),
      cache: "no-store",
    });
    return response.ok ? ((await response.json()) as AuthResponse) : null;
  } catch {
    return null;
  }
}

/**
 * Route gate for page navigations. Keeps the session alive by renewing an
 * expired access token with the refresh cookie (server components cannot set
 * cookies themselves), sends signed-out visitors to /login, keeps signed-in
 * users out of the sign-in pages, and makes sure every page has a CSRF cookie
 * for its forms. API routes handle their own authentication.
 */
export async function proxy(request: NextRequest) {
  const { pathname, search } = request.nextUrl;
  const access = request.cookies.get(names.access)?.value;
  const refreshToken = request.cookies.get(names.refresh)?.value;

  let authenticated = isSessionValid(access);
  let renewed: AuthResponse | null = null;
  let sessionLost = false;
  if (!authenticated && refreshToken) {
    renewed = await refresh(request, refreshToken);
    authenticated = renewed !== null;
    sessionLost = renewed === null;
  }

  let response: NextResponse;
  if (LINK_PAGES.has(pathname)) {
    response = NextResponse.next();
  } else if (AUTH_PAGES.has(pathname)) {
    response = authenticated ? NextResponse.redirect(new URL("/dashboard", request.url)) : NextResponse.next();
  } else if (!authenticated) {
    const login = new URL("/login", request.url);
    if (pathname !== "/") login.searchParams.set("next", pathname + search);
    if (access || refreshToken) login.searchParams.set("reason", "expired");
    response = NextResponse.redirect(login);
  } else if (pathname === "/") {
    response = NextResponse.redirect(new URL("/dashboard", request.url));
  } else if (renewed) {
    // Let the page that renders right now see the new access token as well.
    const headers = new Headers(request.headers);
    const others = request.cookies.getAll().filter((c) => c.name !== names.access && c.name !== names.refresh);
    headers.set(
      "cookie",
      [...others, { name: names.access, value: renewed.accessToken }, { name: names.refresh, value: renewed.refreshToken }]
        .map((c) => `${c.name}=${encodeURIComponent(c.value)}`)
        .join("; "),
    );
    response = NextResponse.next({ request: { headers } });
  } else {
    response = NextResponse.next();
  }

  if (renewed) {
    response.cookies.set(names.access, renewed.accessToken, tokenCookieOptions(renewed.accessTokenExpiresAtUtc, serverConfig.cookieSecure));
    response.cookies.set(names.refresh, renewed.refreshToken, tokenCookieOptions(renewed.refreshTokenExpiresAtUtc, serverConfig.cookieSecure));
  } else if (sessionLost) {
    response.cookies.set(names.access, "", expiredCookieOptions(serverConfig.cookieSecure));
    response.cookies.set(names.refresh, "", expiredCookieOptions(serverConfig.cookieSecure));
  }

  if (!request.cookies.get(names.csrf)?.value) {
    // Readable by this site's scripts on purpose (double-submit pattern); it identifies nothing.
    response.cookies.set(names.csrf, newCsrfToken(), { httpOnly: false, sameSite: "lax", secure: serverConfig.cookieSecure, path: "/" });
  }

  return response;
}

export const config = {
  // Everything except Next internals, static assets and the API routes.
  matcher: ["/((?!api|_next/static|_next/image|favicon.ico|icon.svg|robots.txt).*)"],
};

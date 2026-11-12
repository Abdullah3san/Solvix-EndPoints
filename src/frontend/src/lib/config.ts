/**
 * Server-side configuration. Only read these from route handlers, server
 * components and the proxy; nothing here is exposed to the browser bundle.
 */
export const serverConfig = {
  apiBaseUrl: (process.env.SOLVIX_API_INTERNAL_URL ?? "http://localhost:8080").replace(/\/+$/, ""),
  apiVersion: "v1",
  cookieSecure: process.env.SOLVIX_COOKIE_SECURE === "true" || process.env.NODE_ENV === "production",
  /** Public origin of this site (e.g. https://app.example.com). Used to verify the Origin of state-changing requests. */
  webOrigin: (process.env.SOLVIX_WEB_ORIGIN ?? "").replace(/\/+$/, "") || null,
} as const;

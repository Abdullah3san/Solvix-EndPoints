/**
 * Server-side configuration. Only read these from route handlers, server
 * components and the proxy; nothing here is exposed to the browser bundle.
 */
export const serverConfig = {
  apiBaseUrl: (process.env.SOLVIX_API_INTERNAL_URL ?? "http://localhost:8080").replace(/\/+$/, ""),
  apiVersion: "v1",
  cookieSecure:
    process.env.SOLVIX_COOKIE_SECURE === "true" || process.env.NODE_ENV === "production",
} as const;

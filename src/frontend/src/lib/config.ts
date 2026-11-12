/**
 * Server-side configuration. Only read these from route handlers, server
 * components and the proxy; nothing here is exposed to the browser bundle.
 */
export const serverConfig = {
  apiBaseUrl: (process.env.SOLVIX_API_INTERNAL_URL ?? "http://localhost:8080").replace(/\/+$/, ""),
  apiVersion: "v1",
  /** Public HTTPS URL agents use (mutual TLS endpoint); shown in install instructions. */
  agentServerUrl: process.env.SOLVIX_AGENT_SERVER_URL ?? "https://localhost:8443/",
  /** Optional SHA-256 pin of a private agent TLS certificate, shown in install instructions. */
  agentServerCertificateSha256: process.env.SOLVIX_AGENT_SERVER_CERT_SHA256 ?? null,
  cookieSecure:
    process.env.SOLVIX_COOKIE_SECURE === "true" || process.env.NODE_ENV === "production",
} as const;

"use client";

import { CSRF_HEADER, readCsrfCookie } from "@/lib/auth/csrf";
import { ApiError, problemFromResponse } from "./errors";

/**
 * Browser API client. All calls go through the Next.js BFF at /api/proxy,
 * which attaches the httpOnly session cookie as a bearer token, so the access
 * token is never exposed to client-side JavaScript. State-changing calls echo
 * the CSRF cookie in a header.
 */
export const PROXY_PREFIX = "/api/proxy/v1";

interface ClientOptions extends Omit<RequestInit, "body"> {
  body?: unknown;
  query?: Record<string, string | number | boolean | null | undefined>;
}

function buildUrl(path: string, query?: ClientOptions["query"]): string {
  const clean = path.replace(/^\/+/, "");
  const url = `${PROXY_PREFIX}/${clean}`;
  if (!query) return url;
  const params = new URLSearchParams();
  for (const [key, value] of Object.entries(query)) {
    if (value === undefined || value === null || value === "") continue;
    params.set(key, String(value));
  }
  const qs = params.toString();
  return qs ? `${url}?${qs}` : url;
}

export function csrfHeader(method: string | undefined): Record<string, string> {
  if (!method || method.toUpperCase() === "GET" || typeof document === "undefined") return {};
  const token = readCsrfCookie(document.cookie);
  return token ? { [CSRF_HEADER]: token } : {};
}

/** POST to one of this site's own /api/auth routes. */
export async function authPost<T>(path: string, body?: unknown): Promise<T> {
  const response = await fetch(path, {
    method: "POST",
    credentials: "same-origin",
    headers: { "Content-Type": "application/json", Accept: "application/json", ...csrfHeader("POST") },
    body: JSON.stringify(body ?? {}),
  });
  if (!response.ok) throw new ApiError(response.status, await problemFromResponse(response));
  if (response.status === 204 || response.status === 202) return undefined as T;
  return (await response.json()) as T;
}

export async function apiFetch<T>(path: string, options: ClientOptions = {}): Promise<T> {
  const { body, query, headers, ...init } = options;
  const response = await fetch(buildUrl(path, query), {
    ...init,
    credentials: "same-origin",
    headers: {
      Accept: "application/json",
      ...(body !== undefined ? { "Content-Type": "application/json" } : {}),
      ...csrfHeader(init.method),
      ...(headers as Record<string, string> | undefined),
    },
    body: body !== undefined ? JSON.stringify(body) : undefined,
  });

  if (response.status === 401 && typeof window !== "undefined") {
    // Full navigation on purpose: it drops all client state belonging to the expired session.
    const login = new URL("/login", window.location.origin);
    login.searchParams.set("reason", "expired");
    login.searchParams.set("next", window.location.pathname + window.location.search);
    window.location.assign(login.toString());
  }

  if (!response.ok) {
    throw new ApiError(response.status, await problemFromResponse(response));
  }

  if (response.status === 204 || response.status === 202) {
    return undefined as T;
  }

  return (await response.json()) as T;
}

export const api = {
  get: <T>(path: string, query?: ClientOptions["query"]) => apiFetch<T>(path, { method: "GET", query }),
  post: <T>(path: string, body?: unknown) => apiFetch<T>(path, { method: "POST", body }),
  put: <T>(path: string, body?: unknown) => apiFetch<T>(path, { method: "PUT", body }),
  patch: <T>(path: string, body?: unknown) => apiFetch<T>(path, { method: "PATCH", body }),
  delete: <T = void>(path: string) => apiFetch<T>(path, { method: "DELETE" }),
};

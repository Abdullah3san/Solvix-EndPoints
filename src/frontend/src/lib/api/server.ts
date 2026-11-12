import "server-only";
import { cookies } from "next/headers";
import { redirect } from "next/navigation";
import { serverConfig } from "@/lib/config";
import { cookieNames, isSessionValid } from "@/lib/auth/session";
import { ApiError, problemFromResponse } from "./errors";

/**
 * Server-side API client used by server components and route handlers. It
 * reads the session cookie and forwards it as a bearer token to the API.
 */
export async function getSessionToken(): Promise<string | null> {
  const store = await cookies();
  const token = store.get(cookieNames(serverConfig.cookieSecure).access)?.value;
  return isSessionValid(token) ? token! : null;
}

export function apiUrl(path: string): string {
  const clean = path.replace(/^\/+/, "");
  return `${serverConfig.apiBaseUrl}/api/${serverConfig.apiVersion}/${clean}`;
}

interface ServerFetchOptions extends Omit<RequestInit, "body"> {
  body?: unknown;
  token?: string | null;
  /** When true (default) a 401 redirects to /login instead of throwing. */
  redirectOnUnauthorized?: boolean;
}

export async function serverApi<T>(path: string, options: ServerFetchOptions = {}): Promise<T> {
  const { body, token: explicitToken, redirectOnUnauthorized = true, headers, ...init } = options;
  const token = explicitToken === undefined ? await getSessionToken() : explicitToken;

  const response = await fetch(apiUrl(path), {
    ...init,
    cache: "no-store",
    headers: {
      Accept: "application/json",
      ...(body !== undefined ? { "Content-Type": "application/json" } : {}),
      ...(token ? { Authorization: `Bearer ${token}` } : {}),
      ...(headers as Record<string, string> | undefined),
    },
    body: body !== undefined ? JSON.stringify(body) : undefined,
  });

  if (response.status === 401 && redirectOnUnauthorized) {
    redirect("/login?reason=expired");
  }

  if (!response.ok) {
    throw new ApiError(response.status, await problemFromResponse(response));
  }

  if (response.status === 204) {
    return undefined as T;
  }

  return (await response.json()) as T;
}

/**
 * Like serverApi, but a 403 becomes null so a page can render "no access"
 * instead of an error. The API made the decision; the page only reports it.
 */
export async function serverApiOrForbidden<T>(path: string): Promise<T | null> {
  try {
    return await serverApi<T>(path);
  } catch (error) {
    if (error instanceof ApiError && error.status === 403) return null;
    throw error;
  }
}

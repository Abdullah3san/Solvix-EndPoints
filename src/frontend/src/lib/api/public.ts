import "server-only";
import { serverConfig } from "@/lib/config";
import type { PasswordRulesDto } from "./types";

/** Platform password rules for anonymous forms. Null when the API cannot be reached; the API still enforces them. */
export async function fetchPasswordRules(): Promise<PasswordRulesDto | null> {
  try {
    const response = await fetch(`${serverConfig.apiBaseUrl}/api/${serverConfig.apiVersion}/auth/password-rules`, { cache: "no-store" });
    return response.ok ? ((await response.json()) as PasswordRulesDto) : null;
  } catch {
    return null;
  }
}

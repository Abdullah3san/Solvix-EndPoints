import { ApiError } from "@/lib/api/errors";

/** Flattens API problem-details field errors to "first message per field". */
export function fieldErrorsFrom(error: unknown): Record<string, string> {
  if (!(error instanceof ApiError)) return {};
  const result: Record<string, string> = {};
  for (const [key, messages] of Object.entries(error.fieldErrors)) result[key] = messages[0];
  return result;
}

export function messageFrom(error: unknown, fallback = "Something went wrong."): string {
  if (error instanceof ApiError) return error.problem.detail ?? error.problem.title ?? fallback;
  if (error instanceof Error) return error.message || fallback;
  return fallback;
}

export function emptyToNull(value: string | null | undefined): string | null {
  const trimmed = value?.trim() ?? "";
  return trimmed.length === 0 ? null : trimmed;
}

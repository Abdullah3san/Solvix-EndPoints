import type { ProblemDetails } from "./types";

/** Thrown by both the server and browser API clients for non-2xx responses. */
export class ApiError extends Error {
  readonly status: number;
  readonly code: string;
  readonly problem: ProblemDetails;

  constructor(status: number, problem: ProblemDetails) {
    super(problem.detail || problem.title || `Request failed with status ${status}`);
    this.name = "ApiError";
    this.status = status;
    this.code = problem.code ?? (status === 401 ? "unauthorized" : status === 403 ? "forbidden" : "error");
    this.problem = problem;
  }

  /** Field-level errors keyed by camelCase property path. */
  get fieldErrors(): Record<string, string[]> {
    return this.problem.errors ?? {};
  }

  firstFieldError(field: string): string | undefined {
    return this.fieldErrors[field]?.[0];
  }
}

export async function problemFromResponse(response: Response): Promise<ProblemDetails> {
  const contentType = response.headers.get("content-type") ?? "";
  if (contentType.includes("json")) {
    try {
      return (await response.json()) as ProblemDetails;
    } catch {
      // fall through
    }
  }
  const text = await response.text().catch(() => "");
  return { status: response.status, title: response.statusText || "Request failed", detail: text || undefined };
}

export function isApiError(error: unknown): error is ApiError {
  return error instanceof ApiError;
}

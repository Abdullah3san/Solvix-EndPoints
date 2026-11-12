import { afterEach, describe, expect, it, vi } from "vitest";
import { api } from "@/lib/api/client";
import { ApiError } from "@/lib/api/errors";

describe("browser api client", () => {
  afterEach(() => vi.restoreAllMocks());

  it("routes through the BFF proxy with query parameters", async () => {
    const fetchMock = vi.spyOn(globalThis, "fetch").mockResolvedValue(
      new Response(JSON.stringify({ items: [] }), { status: 200, headers: { "content-type": "application/json" } }),
    );
    await api.get("branches", { search: "ber", includeInactive: false, page: 2, empty: "" });
    expect(fetchMock).toHaveBeenCalledWith("/api/proxy/v1/branches?search=ber&includeInactive=false&page=2", expect.objectContaining({ method: "GET" }));
  });

  it("converts problem+json responses into ApiError with field errors", async () => {
    vi.spyOn(globalThis, "fetch").mockResolvedValue(
      new Response(JSON.stringify({ title: "Validation failed", status: 400, code: "validation_failed", errors: { code: ["Code is required."] } }), {
        status: 400,
        headers: { "content-type": "application/problem+json" },
      }),
    );
    const error = await api.post("branches", {}).catch((e: unknown) => e);
    expect(error).toBeInstanceOf(ApiError);
    const apiError = error as ApiError;
    expect(apiError.status).toBe(400);
    expect(apiError.code).toBe("validation_failed");
    expect(apiError.firstFieldError("code")).toBe("Code is required.");
  });
});

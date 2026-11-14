import "server-only";
import { notFound } from "next/navigation";
import { ApiError } from "./errors";

/** Server-component helper: a 403/404 from the API (missing permission or unknown record) becomes a 404 page. */
export async function adminOr404<T>(promise: Promise<T>): Promise<T> {
  try {
    return await promise;
  } catch (e) {
    if (e instanceof ApiError && (e.status === 403 || e.status === 404)) notFound();
    throw e;
  }
}

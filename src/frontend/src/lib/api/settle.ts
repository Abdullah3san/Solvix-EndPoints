import { unstable_rethrow } from "next/navigation";
import { messageFrom } from "@/lib/forms";

export type Settled<T> = { ok: true; value: T } | { ok: false; message: string };

/** Awaits a server call and turns failure into data, so one failing source degrades its own widget instead of the page. */
export async function settle<T>(promise: Promise<T>, fallback = "The request failed."): Promise<Settled<T>> {
  try {
    return { ok: true, value: await promise };
  } catch (error) {
    // Redirects (expired session) and not-found must keep propagating.
    unstable_rethrow(error);
    return { ok: false, message: messageFrom(error, fallback) };
  }
}

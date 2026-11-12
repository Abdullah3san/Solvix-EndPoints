const ALLOWED_HUBS = new Set(["monitoring"]);

/** The realtime BFF only forwards to known hubs: /hubs/{hub} and /hubs/{hub}/negotiate. */
export function isAllowedRealtimePath(path: string[] | undefined): boolean {
  if (!path || path.length < 2 || path.length > 3 || path[0] !== "hubs" || !ALLOWED_HUBS.has(path[1])) return false;
  return path.length === 2 || path[2] === "negotiate";
}

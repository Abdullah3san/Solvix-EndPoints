/**
 * Environment switches that are safe to evaluate on the server and the client.
 *
 * Demo data exists so the UI can be developed before every backend module is
 * available. It is deliberately hard to turn on by accident:
 *   1. only under `next dev` (NODE_ENV === "development"), and
 *   2. only when NEXT_PUBLIC_SOLVIX_DEMO_DATA=true is set explicitly.
 * A production build ignores the flag, and every screen that uses demo data
 * labels it. See docs/DESIGN-SYSTEM.md, "Data honesty".
 */
export const isDevelopment = process.env.NODE_ENV === "development";

export function isDemoDataEnabled(env: { NODE_ENV?: string; NEXT_PUBLIC_SOLVIX_DEMO_DATA?: string } = {
  NODE_ENV: process.env.NODE_ENV,
  NEXT_PUBLIC_SOLVIX_DEMO_DATA: process.env.NEXT_PUBLIC_SOLVIX_DEMO_DATA,
}): boolean {
  return env.NODE_ENV === "development" && env.NEXT_PUBLIC_SOLVIX_DEMO_DATA === "true";
}

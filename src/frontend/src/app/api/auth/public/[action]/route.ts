import { NextRequest } from "next/server";
import { passThrough, postUpstream, problem, rejectCsrf } from "@/lib/auth/bff";

/**
 * Anonymous account flows that neither create nor need a session. A fixed
 * allow-list maps each action to one API endpoint, so this route cannot be
 * used to reach anything else upstream.
 */
const ACTIONS: Record<string, string> = {
  "verify-email": "auth/verify-email",
  "resend-verification": "auth/resend-verification",
  "forgot-password": "auth/forgot-password",
  "reset-password": "auth/reset-password",
  "invitation-preview": "auth/invitations/preview",
  "invitation-accept": "auth/invitations/accept",
};

export async function POST(request: NextRequest, context: { params: Promise<{ action: string }> }) {
  const rejected = rejectCsrf(request);
  if (rejected) return rejected;

  const { action } = await context.params;
  const path = Object.hasOwn(ACTIONS, action) ? ACTIONS[action] : null;
  if (!path) return problem(404, "not_found", "Not found");

  return passThrough(await postUpstream(request, path, await request.json()));
}

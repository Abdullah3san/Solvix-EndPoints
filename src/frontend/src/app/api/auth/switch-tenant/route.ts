import { NextRequest, NextResponse } from "next/server";
import type { AuthResponse } from "@/lib/api/types";
import { names, passThrough, postUpstream, problem, publicSession, rejectCsrf, setSessionCookies } from "@/lib/auth/bff";
import { isSessionValid } from "@/lib/auth/session";

export async function POST(request: NextRequest) {
  const rejected = rejectCsrf(request);
  if (rejected) return rejected;

  const token = request.cookies.get(names.access)?.value;
  if (!isSessionValid(token)) return problem(401, "unauthorized", "Unauthorized", "Your session has expired.");

  const upstream = await postUpstream(request, "auth/switch-tenant", await request.json(), token);
  if (!upstream.ok) return passThrough(upstream);

  const auth = (await upstream.json()) as AuthResponse;
  const response = NextResponse.json({ session: publicSession(auth) }, { headers: { "cache-control": "no-store" } });
  setSessionCookies(response, auth);
  return response;
}

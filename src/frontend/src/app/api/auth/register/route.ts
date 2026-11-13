import { NextRequest, NextResponse } from "next/server";
import type { RegisterTenantResponse } from "@/lib/api/types";
import { passThrough, postUpstream, publicSession, rejectCsrf, setSessionCookies } from "@/lib/auth/bff";

export async function POST(request: NextRequest) {
  const rejected = rejectCsrf(request);
  if (rejected) return rejected;

  const upstream = await postUpstream(request, "tenant/register", await request.json());
  if (!upstream.ok) return passThrough(upstream);

  const result = (await upstream.json()) as RegisterTenantResponse;
  const response = NextResponse.json(
    {
      tenantId: result.tenantId,
      slug: result.slug,
      name: result.name,
      emailVerificationRequired: result.emailVerificationRequired,
      session: result.session ? publicSession(result.session) : null,
    },
    { status: 201, headers: { "cache-control": "no-store" } },
  );

  // Only when the deployment does not require e-mail verification is the owner signed in straight away.
  if (result.session) setSessionCookies(response, result.session);
  return response;
}

import type { Metadata } from "next";
import { serverApi } from "@/lib/api/server";
import type { MfaStatusDto, PasswordRulesDto, SessionDto } from "@/lib/api/types";
import { PageHeader } from "@/components/ui/page-header";
import { AccountSecurity } from "@/components/features/account-security";

export const metadata: Metadata = { title: "My account" };

export default async function AccountSecurityPage() {
  const [mfa, sessions, rules] = await Promise.all([
    serverApi<MfaStatusDto>("auth/mfa"),
    serverApi<SessionDto[]>("auth/sessions"),
    serverApi<PasswordRulesDto>("auth/me/password-rules"),
  ]);
  return (
    <>
      <PageHeader title="My account" description="Your password, two-step verification and signed-in devices. These belong to you in every workspace." />
      <AccountSecurity mfa={mfa} sessions={sessions} rules={rules} />
    </>
  );
}

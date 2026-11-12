import type { Metadata } from "next";
import { serverApiOrForbidden } from "@/lib/api/server";
import type { SecurityPolicyDto } from "@/lib/api/types";
import { PageHeader } from "@/components/ui/page-header";
import { NoAccess } from "@/components/features/no-access";
import { SecurityPolicyForm } from "@/components/features/security-policy-form";

export const metadata: Metadata = { title: "Security policy" };

export default async function SecurityPolicyPage() {
  const policy = await serverApiOrForbidden<SecurityPolicyDto>("security/policy");
  return (
    <>
      <PageHeader title="Security policy" description="Password rules, session lifetimes and multi-factor authentication for this workspace." />
      {policy ? <SecurityPolicyForm initial={policy} /> : <NoAccess what="the security policy" />}
    </>
  );
}

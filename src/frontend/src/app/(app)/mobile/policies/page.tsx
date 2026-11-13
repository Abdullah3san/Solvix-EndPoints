import type { Metadata } from "next";
import { serverApi } from "@/lib/api/server";
import type { PolicyDto } from "@/lib/api/mdm-types";
import type { PagedResult } from "@/lib/api/types";
import { PageHeader } from "@/components/ui/page-header";
import { PoliciesView } from "@/components/features/mobile/policies-view";

export const metadata: Metadata = { title: "Mobile policies" };

export default async function MobilePoliciesPage() {
  const policies = await serverApi<PagedResult<PolicyDto>>("mobile-policies?pageSize=100");
  return (
    <>
      <PageHeader title="Mobile policies" description="Passcode, Wi-Fi, VPN, restrictions, apps and compliance rules per device type." />
      <PoliciesView initial={policies} />
    </>
  );
}

import type { Metadata } from "next";
import { serverApi } from "@/lib/api/server";
import type { EnrollmentDto, EnterpriseDto, PolicyDto } from "@/lib/api/mdm-types";
import type { PagedResult } from "@/lib/api/types";
import { PageHeader } from "@/components/ui/page-header";
import { EnrollmentsView } from "@/components/features/mobile/enrollments-view";

export const metadata: Metadata = { title: "Enrollment" };

export default async function MobileEnrollmentsPage() {
  const [enrollments, android, policies] = await Promise.all([
    serverApi<PagedResult<EnrollmentDto>>("mobile-enrollments?pageSize=50"),
    serverApi<EnterpriseDto>("mobile-enterprise/Android"),
    serverApi<PagedResult<PolicyDto>>("mobile-policies?pageSize=100"),
  ]);
  return (
    <>
      <PageHeader title="Enrollment" description="Invite Android and iOS devices into management." />
      <EnrollmentsView initial={enrollments} android={android} policies={policies.items} />
    </>
  );
}

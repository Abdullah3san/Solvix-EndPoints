import type { Metadata } from "next";
import { redirect } from "next/navigation";
import { serverApi } from "@/lib/api/server";
import { ApiError } from "@/lib/api/errors";
import type { BranchDto, DepartmentDto, DeploymentProfileDto, DeviceGroupDto, EnrollmentTokenDto, PagedResult, PendingEnrollmentDto } from "@/lib/api/types";
import { PageHeader } from "@/components/ui/page-header";
import { EnrollmentView } from "@/components/features/devices/enrollment-view";

export const metadata: Metadata = { title: "Enrollment" };

export default async function EnrollmentPage() {
  let tokens: PagedResult<EnrollmentTokenDto>;
  try {
    tokens = await serverApi<PagedResult<EnrollmentTokenDto>>("enrollment-tokens?includeInactive=true&pageSize=50");
  } catch (e) {
    if (e instanceof ApiError && e.status === 403) redirect("/endpoints/devices");
    throw e;
  }

  const [profiles, pending, groups, branches, departments] = await Promise.all([
    serverApi<DeploymentProfileDto[]>("deployment-profiles"),
    serverApi<PagedResult<PendingEnrollmentDto>>("pending-enrollments?status=Pending&pageSize=100"),
    serverApi<PagedResult<DeviceGroupDto>>("device-groups?pageSize=200"),
    serverApi<PagedResult<BranchDto>>("branches?pageSize=200"),
    serverApi<PagedResult<DepartmentDto>>("departments?pageSize=200"),
  ]);
  return (
    <>
      <PageHeader title="Enrollment" description="Download the Windows installer (.exe), run it on a computer, then approve it here. Deployment profiles decide where approved computers are placed; one-time tokens remain available for scripted rollouts." />
      <EnrollmentView initial={tokens} groups={groups.items} branches={branches.items} departments={departments.items} profiles={profiles} pending={pending.items} />
    </>
  );
}

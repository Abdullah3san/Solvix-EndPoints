import type { Metadata } from "next";
import { redirect } from "next/navigation";
import { serverApi } from "@/lib/api/server";
import { ApiError } from "@/lib/api/errors";
import type { BranchDto, DepartmentDto, DeviceGroupDto, EnrollmentTokenDto, PagedResult } from "@/lib/api/types";
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

  const [groups, branches, departments] = await Promise.all([
    serverApi<PagedResult<DeviceGroupDto>>("device-groups?pageSize=200"),
    serverApi<PagedResult<BranchDto>>("branches?pageSize=200"),
    serverApi<PagedResult<DepartmentDto>>("departments?pageSize=200"),
  ]);
  return (
    <>
      <PageHeader title="Enrollment" description="Short-lived enrollment tokens authorise Windows devices to join this workspace. A token is shown once; the downloaded package carries it in a separate file next to the signed agent, never inside the exe." />
      <EnrollmentView initial={tokens} groups={groups.items} branches={branches.items} departments={departments.items} />
    </>
  );
}

import type { Metadata } from "next";
import { redirect } from "next/navigation";
import { serverApi } from "@/lib/api/server";
import { ApiError } from "@/lib/api/errors";
import { serverConfig } from "@/lib/config";
import type { DeviceGroupDto, EnrollmentTokenDto, PagedResult } from "@/lib/api/types";
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

  const groups = await serverApi<PagedResult<DeviceGroupDto>>("device-groups?pageSize=200");
  return (
    <>
      <PageHeader title="Enrollment" description="Short-lived enrollment tokens authorise Windows devices to join this workspace. Tokens are shown once and never embedded in the installer." />
      <EnrollmentView initial={tokens} groups={groups.items} serverUrl={serverConfig.agentServerUrl} serverPin={serverConfig.agentServerCertificateSha256} />
    </>
  );
}

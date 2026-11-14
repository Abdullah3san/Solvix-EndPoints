import type { Metadata } from "next";
import { serverApi } from "@/lib/api/server";
import type { CommandDto, DeviceDto } from "@/lib/api/mdm-types";
import type { PagedResult } from "@/lib/api/types";
import { PageHeader } from "@/components/ui/page-header";
import { ApprovalsView } from "@/components/features/mobile/approvals-view";

export const metadata: Metadata = { title: "Approvals" };

export default async function MobileApprovalsPage() {
  const [commands, devices] = await Promise.all([
    serverApi<PagedResult<CommandDto>>("mobile-commands?state=PendingApproval&pageSize=100"),
    serverApi<PagedResult<DeviceDto>>("mobile-devices?pageSize=200"),
  ]);
  return (
    <>
      <PageHeader title="Approvals" description="Destructive device commands waiting for a second administrator." />
      <ApprovalsView initial={commands} devices={devices.items} />
    </>
  );
}

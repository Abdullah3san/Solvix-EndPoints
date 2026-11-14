import type { Metadata } from "next";
import { serverApi } from "@/lib/api/server";
import type { ComplianceSummaryDto, DeviceDto } from "@/lib/api/mdm-types";
import type { PagedResult } from "@/lib/api/types";
import { PageHeader } from "@/components/ui/page-header";
import { DevicesView } from "@/components/features/mobile/devices-view";

export const metadata: Metadata = { title: "Mobile devices" };

export default async function MobileDevicesPage() {
  const [devices, summary] = await Promise.all([
    serverApi<PagedResult<DeviceDto>>("mobile-devices?pageSize=25"),
    serverApi<ComplianceSummaryDto>("mobile-compliance/summary"),
  ]);
  return (
    <>
      <PageHeader title="Mobile devices" description="Android and iOS devices managed by your organisation." />
      <DevicesView initial={devices} summary={summary} />
    </>
  );
}

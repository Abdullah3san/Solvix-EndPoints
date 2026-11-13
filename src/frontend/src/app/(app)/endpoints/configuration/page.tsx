import type { Metadata } from "next";
import { serverApi } from "@/lib/api/server";
import type { PagedResult } from "@/lib/api/types";
import type { BaselineDto, ConfigurationComplianceDto, ConfigurationPolicySummaryDto, DeviceConflictsDto, DriftEventDto } from "@/lib/api/endpoint-types";
import { PageHeader } from "@/components/ui/page-header";
import { ConfigurationView } from "@/components/features/endpoints/configuration-view";

export const metadata: Metadata = { title: "Configuration" };

export default async function ConfigurationPage() {
  const [policies, compliance, conflicts, drift, baselines] = await Promise.all([
    serverApi<ConfigurationPolicySummaryDto[]>("configuration-policies"),
    serverApi<ConfigurationComplianceDto>("configuration/compliance"),
    serverApi<DeviceConflictsDto[]>("configuration/conflicts?includeResolved=true"),
    serverApi<PagedResult<DriftEventDto>>("configuration/drift?pageSize=20"),
    serverApi<BaselineDto[]>("configuration/baselines"),
  ]);
  return (
    <>
      <PageHeader title="Configuration" description="Services, registry, startup apps, firewall, power, screen lock and USB policies." />
      <ConfigurationView policies={policies} compliance={compliance} conflicts={conflicts} drift={drift} baselines={baselines} />
    </>
  );
}

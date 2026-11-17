import type { Metadata } from "next";
import { serverApi } from "@/lib/api/server";
import { loadTargetOptions } from "@/lib/api/endpoint-server";
import type { ConfigurationPolicyDto, MaintenanceWindowDto } from "@/lib/api/endpoint-types";
import { PageHeader } from "@/components/ui/page-header";
import { PolicyDetailView } from "@/components/features/endpoints/policy-detail-view";

export const metadata: Metadata = { title: "Configuration policy" };

export default async function PolicyPage({ params }: { params: Promise<{ id: string }> }) {
  const { id } = await params;
  const [policy, targets, windows] = await Promise.all([
    serverApi<ConfigurationPolicyDto>(`configuration-policies/${encodeURIComponent(id)}`),
    loadTargetOptions(),
    serverApi<MaintenanceWindowDto[]>("maintenance-windows"),
  ]);
  return (
    <>
      <PageHeader title={policy.name} description={policy.description ?? "Configuration policy"} />
      <PolicyDetailView initial={policy} targets={targets} windows={windows} />
    </>
  );
}

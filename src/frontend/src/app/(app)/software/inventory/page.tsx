import type { Metadata } from "next";
import { serverApi } from "@/lib/api/server";
import type { ComplianceReport, PagedResult, SoftwareCatalogEntryDto } from "@/lib/api/types";
import { PageHeader } from "@/components/ui/page-header";
import { InventoryView } from "@/components/features/software/inventory-view";

export const metadata: Metadata = { title: "Software inventory" };

export default async function InventoryPage() {
  const [catalog, compliance] = await Promise.all([
    serverApi<PagedResult<SoftwareCatalogEntryDto>>("software/inventory/catalog?pageSize=50"),
    serverApi<ComplianceReport>("software/compliance"),
  ]);
  return (
    <>
      <PageHeader title="Software inventory" description="Applications reported by the agents, and devices that violate software restriction rules." />
      <InventoryView initial={catalog.items} compliance={compliance} />
    </>
  );
}

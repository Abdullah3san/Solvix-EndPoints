import type { Metadata } from "next";
import { serverApi } from "@/lib/api/server";
import type { AppDto } from "@/lib/api/mdm-types";
import type { PagedResult } from "@/lib/api/types";
import { PageHeader } from "@/components/ui/page-header";
import { AppsView } from "@/components/features/mobile/apps-view";

export const metadata: Metadata = { title: "Mobile apps" };

export default async function MobileAppsPage() {
  const apps = await serverApi<PagedResult<AppDto>>("mobile-apps?pageSize=50");
  return (
    <>
      <PageHeader title="Mobile apps" description="Catalogue of apps that policies can deploy, require or block." />
      <AppsView initial={apps} />
    </>
  );
}

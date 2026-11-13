import type { Metadata } from "next";
import { serverApi } from "@/lib/api/server";
import type { TenantDto } from "@/lib/api/types";
import { PageHeader } from "@/components/ui/page-header";
import { WorkspaceSettings } from "@/components/features/workspace-settings";

export const metadata: Metadata = { title: "Workspace settings" };

export default async function WorkspacePage() {
  const tenant = await serverApi<TenantDto>("tenant");
  return (
    <>
      <PageHeader title="Workspace settings" description="Account-level settings of this tenant. People and roles are managed under Access." />
      <WorkspaceSettings tenant={tenant} />
    </>
  );
}

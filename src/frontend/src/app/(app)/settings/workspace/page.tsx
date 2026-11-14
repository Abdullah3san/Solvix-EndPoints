import type { Metadata } from "next";
import { serverApi } from "@/lib/api/server";
import type { MemberDto, PagedResult, TenantDto } from "@/lib/api/types";
import { PageHeader } from "@/components/ui/page-header";
import { WorkspaceSettings } from "@/components/features/workspace-settings";

export const metadata: Metadata = { title: "Workspace settings" };

export default async function WorkspacePage() {
  const [tenant, members] = await Promise.all([
    serverApi<TenantDto>("tenant"),
    serverApi<PagedResult<MemberDto>>("tenant/members?pageSize=100"),
  ]);
  return (
    <>
      <PageHeader title="Workspace settings" description="Manage this tenant and the people who belong to it." />
      <WorkspaceSettings tenant={tenant} members={members} />
    </>
  );
}

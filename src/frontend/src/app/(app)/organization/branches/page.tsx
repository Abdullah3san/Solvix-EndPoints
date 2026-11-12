import type { Metadata } from "next";
import { serverApi } from "@/lib/api/server";
import type { BranchDto, PagedResult } from "@/lib/api/types";
import { PageHeader } from "@/components/ui/page-header";
import { BranchesView } from "@/components/features/branches-view";

export const metadata: Metadata = { title: "Branches" };

export default async function BranchesPage() {
  const branches = await serverApi<PagedResult<BranchDto>>("branches?pageSize=25");
  return (
    <>
      <PageHeader title="Branches" description="Sites and locations of your organisation." />
      <BranchesView initial={branches} />
    </>
  );
}

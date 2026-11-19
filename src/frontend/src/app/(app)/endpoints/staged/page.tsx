import type { Metadata } from "next";
import { serverApi } from "@/lib/api/server";
import type { BranchDto, DepartmentDto, PagedResult, StagedComputerDto, StagedComputerStatus, StagedComputerSummaryDto } from "@/lib/api/types";
import { PageHeader } from "@/components/ui/page-header";
import { StagedComputersView } from "@/components/features/agent/staged-computers-view";

export const metadata: Metadata = { title: "Staged computers" };

const STATUSES: StagedComputerStatus[] = ["Pending", "Enrolled", "Retired"];

export default async function StagedComputersPage({ searchParams }: { searchParams: Promise<{ status?: string }> }) {
  const { status } = await searchParams;
  const initialStatus = STATUSES.find((s) => s === status) ?? "";
  const [list, summary, branches, departments] = await Promise.all([
    serverApi<PagedResult<StagedComputerDto>>(`staged-computers?pageSize=25${initialStatus ? `&status=${initialStatus}` : ""}`),
    serverApi<StagedComputerSummaryDto>("staged-computers/summary"),
    serverApi<PagedResult<BranchDto>>("branches?pageSize=200"),
    serverApi<PagedResult<DepartmentDto>>("departments?pageSize=200"),
  ]);
  return (
    <>
      <PageHeader
        title="Staged computers"
        description="Computers you expect to manage. Each one is flagged until its agent is installed and is marked enrolled as soon as it reports in."
      />
      <StagedComputersView initial={list} summary={summary} branches={branches.items} departments={departments.items} initialStatus={initialStatus} />
    </>
  );
}

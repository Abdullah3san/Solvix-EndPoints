import type { Metadata } from "next";
import { serverApi } from "@/lib/api/server";
import type { BranchDto, DepartmentDto, DepartmentTreeNode, PagedResult } from "@/lib/api/types";
import { PageHeader } from "@/components/ui/page-header";
import { DepartmentsView } from "@/components/features/departments-view";

export const metadata: Metadata = { title: "Departments" };

export default async function DepartmentsPage({ searchParams }: { searchParams: Promise<{ branch?: string }> }) {
  const { branch } = await searchParams;
  const branches = await serverApi<PagedResult<BranchDto>>("branches?includeInactive=true&pageSize=200");
  const selected = branches.items.find((b) => b.id === branch) ?? branches.items.find((b) => b.isHeadquarters) ?? branches.items[0] ?? null;

  const empty: PagedResult<DepartmentDto> = { items: [], page: 1, pageSize: 100, totalCount: 0, totalPages: 0 };
  const [departments, tree] = selected
    ? await Promise.all([
        serverApi<PagedResult<DepartmentDto>>(`departments?branchId=${selected.id}&includeInactive=true&pageSize=100`),
        serverApi<DepartmentTreeNode[]>(`departments/tree/${selected.id}`),
      ])
    : [empty, [] as DepartmentTreeNode[]];

  return (
    <>
      <PageHeader title="Departments" description="Organisational units inside each branch. Departments can be nested." />
      <DepartmentsView branches={branches.items} initialBranchId={selected?.id ?? null} initialDepartments={departments} initialTree={tree} />
    </>
  );
}

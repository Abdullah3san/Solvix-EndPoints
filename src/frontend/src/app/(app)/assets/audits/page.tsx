import type { Metadata } from "next";
import { serverApi } from "@/lib/api/server";
import { adminOr404 } from "@/lib/api/require-admin";
import type { BranchDto, DepartmentDto, PagedResult, PhysicalAuditDto } from "@/lib/api/types";
import { AuditsList } from "@/components/features/assets/audits-view";

export const metadata: Metadata = { title: "Physical audits" };

export default async function AuditsPage() {
  const [audits, branches, departments] = await Promise.all([
    adminOr404(serverApi<PagedResult<PhysicalAuditDto>>("asset-audits?pageSize=50")),
    serverApi<PagedResult<BranchDto>>("branches?pageSize=200"),
    serverApi<PagedResult<DepartmentDto>>("departments?pageSize=200"),
  ]);
  return <AuditsList initial={audits} branches={branches.items} departments={departments.items} />;
}

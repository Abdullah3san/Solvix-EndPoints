import type { Metadata } from "next";
import { serverApi } from "@/lib/api/server";
import { adminOr404 } from "@/lib/api/require-admin";
import type { AssetStatsDto, AssetSummaryDto, BranchDto, DepartmentDto, PagedResult, VendorDto } from "@/lib/api/types";
import { parseRegisterQuery, registerQuery } from "@/lib/assets";
import { PageHeader } from "@/components/ui/page-header";
import { AssetRegister } from "@/components/features/assets/asset-register";

export const metadata: Metadata = { title: "Asset register" };

export default async function AssetsPage({ searchParams }: { searchParams: Promise<Record<string, string | string[] | undefined>> }) {
  const { page, ...filters } = parseRegisterQuery(await searchParams);
  const query = registerQuery(filters, page);
  query.set("pageSize", String(filters.pageSize));
  const [assets, stats, branches, departments, vendors] = await Promise.all([
    adminOr404(serverApi<PagedResult<AssetSummaryDto>>(`assets?${query}`)),
    adminOr404(serverApi<AssetStatsDto>("assets/stats")),
    serverApi<PagedResult<BranchDto>>("branches?pageSize=200"),
    serverApi<PagedResult<DepartmentDto>>("departments?pageSize=200"),
    adminOr404(serverApi<PagedResult<VendorDto>>("asset-vendors?pageSize=200&includeInactive=true")),
  ]);
  return (
    <>
      <PageHeader title="Asset register" description="Every IT asset across branches: hardware with and without the agent, from purchase to disposal." />
      <AssetRegister initial={assets} initialFilters={filters} stats={stats} branches={branches.items} departments={departments.items} vendors={vendors.items} />
    </>
  );
}

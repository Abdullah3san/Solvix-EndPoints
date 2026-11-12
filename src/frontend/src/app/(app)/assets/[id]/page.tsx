import type { Metadata } from "next";
import { notFound } from "next/navigation";
import { serverApi } from "@/lib/api/server";
import { adminOr404 } from "@/lib/api/require-admin";
import type { AssetDetailsDto, BranchDto, DepartmentDto, MemberDto, PagedResult, VendorDto } from "@/lib/api/types";
import { AssetDetailsView } from "@/components/features/assets/asset-details";

export const metadata: Metadata = { title: "Asset" };

export default async function AssetPage({ params }: { params: Promise<{ id: string }> }) {
  const { id } = await params;
  if (!/^[0-9a-f-]{36}$/i.test(id)) notFound();
  const [asset, members, branches, departments, vendors] = await Promise.all([
    adminOr404(serverApi<AssetDetailsDto>(`assets/${id}`)),
    serverApi<PagedResult<MemberDto>>("tenant/members?pageSize=200"),
    serverApi<PagedResult<BranchDto>>("branches?pageSize=200"),
    serverApi<PagedResult<DepartmentDto>>("departments?pageSize=200"),
    adminOr404(serverApi<PagedResult<VendorDto>>("asset-vendors?pageSize=200")),
  ]);
  return <AssetDetailsView initial={asset} members={members.items} branches={branches.items} departments={departments.items} vendors={vendors.items} />;
}

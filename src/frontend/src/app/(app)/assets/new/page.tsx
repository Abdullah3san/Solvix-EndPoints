import type { Metadata } from "next";
import { serverApi, serverApiOrForbidden } from "@/lib/api/server";
import { adminOr404 } from "@/lib/api/require-admin";
import type { AssetSettingsDto, BranchDto, DepartmentDto, TenantUserDto, PagedResult, VendorDto } from "@/lib/api/types";
import { PageHeader } from "@/components/ui/page-header";
import { NewAssetView } from "@/components/features/assets/new-asset-view";

export const metadata: Metadata = { title: "Register asset" };

export default async function NewAssetPage() {
  const [settings, branches, departments, vendors, members] = await Promise.all([
    adminOr404(serverApi<AssetSettingsDto>("asset-settings")),
    serverApi<PagedResult<BranchDto>>("branches?pageSize=200"),
    serverApi<PagedResult<DepartmentDto>>("departments?pageSize=200"),
    adminOr404(serverApi<PagedResult<VendorDto>>("asset-vendors?pageSize=200")),
    serverApiOrForbidden<PagedResult<TenantUserDto>>("users?pageSize=200"),
  ]);
  return (
    <>
      <PageHeader title="Register asset" description="The asset tag is generated automatically unless you enter an existing label." />
      <NewAssetView branches={branches.items} departments={departments.items} vendors={vendors.items} members={members?.items ?? []} defaultCurrency={settings.defaultCurrency} />
    </>
  );
}

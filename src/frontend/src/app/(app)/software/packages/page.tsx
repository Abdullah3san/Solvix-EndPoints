import type { Metadata } from "next";
import { serverApi } from "@/lib/api/server";
import type { PackageDto, PagedResult } from "@/lib/api/types";
import { PageHeader } from "@/components/ui/page-header";
import { PackagesView } from "@/components/features/software/packages-view";

export const metadata: Metadata = { title: "Software packages" };

export default async function PackagesPage() {
  const packages = await serverApi<PagedResult<PackageDto>>("software/packages?pageSize=100");
  return (
    <>
      <PageHeader title="Software packages" description="MSI and EXE installers are validated, checksummed and stored securely. Uploading never runs an installer." />
      <PackagesView initial={packages.items} />
    </>
  );
}

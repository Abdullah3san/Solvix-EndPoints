import type { Metadata } from "next";
import { serverApi } from "@/lib/api/server";
import { adminOr404 } from "@/lib/api/require-admin";
import type { PagedResult, VendorDto } from "@/lib/api/types";
import { VendorsView } from "@/components/features/assets/vendors-view";

export const metadata: Metadata = { title: "Vendors" };

export default async function VendorsPage() {
  const vendors = await adminOr404(serverApi<PagedResult<VendorDto>>("asset-vendors?pageSize=200&includeInactive=true"));
  return <VendorsView initial={vendors} />;
}

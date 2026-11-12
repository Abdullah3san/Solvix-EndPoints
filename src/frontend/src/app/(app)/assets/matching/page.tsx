import type { Metadata } from "next";
import { serverApi } from "@/lib/api/server";
import { adminOr404 } from "@/lib/api/require-admin";
import type { AssetDeviceLinkDto, EndpointDto, PagedResult } from "@/lib/api/types";
import { PageHeader } from "@/components/ui/page-header";
import { MatchingView } from "@/components/features/assets/matching-view";

export const metadata: Metadata = { title: "Endpoint matching" };

export default async function MatchingPage({ searchParams }: { searchParams: Promise<{ assetId?: string }> }) {
  const { assetId: raw } = await searchParams;
  const assetId = raw && /^[0-9a-f-]{36}$/i.test(raw) ? raw : undefined;
  const [links, unregistered] = await Promise.all([
    adminOr404(serverApi<PagedResult<AssetDeviceLinkDto>>(`asset-matches?status=Suggested&pageSize=25${assetId ? `&assetId=${assetId}` : ""}`)),
    adminOr404(serverApi<PagedResult<EndpointDto>>("asset-matches/unregistered-endpoints?pageSize=25")),
  ]);
  return (
    <>
      <PageHeader
        title="Endpoint matching"
        description="Managed Windows endpoints are matched to physical assets by serial number and manufacturer/model. Review each proposal; ambiguous matches are never linked automatically and records are never merged."
      />
      <MatchingView initial={links} unregistered={unregistered} assetId={assetId} />
    </>
  );
}

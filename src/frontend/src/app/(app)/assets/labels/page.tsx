import type { Metadata } from "next";
import { serverApi } from "@/lib/api/server";
import { adminOr404 } from "@/lib/api/require-admin";
import type { AssetDetailsDto, CurrentUserDto } from "@/lib/api/types";
import { LabelSheet } from "@/components/features/assets/label-sheet";

export const metadata: Metadata = { title: "Asset labels" };

const MAX_LABELS = 200;

export default async function LabelsPage({ searchParams }: { searchParams: Promise<{ ids?: string }> }) {
  const { ids = "" } = await searchParams;
  const unique = [...new Set(ids.split(",").filter((id) => /^[0-9a-f-]{36}$/i.test(id)))].slice(0, MAX_LABELS);
  const [me, ...assets] = await Promise.all([serverApi<CurrentUserDto>("auth/me"), ...unique.map((id) => adminOr404(serverApi<AssetDetailsDto>(`assets/${id}`)))]);
  return <LabelSheet assets={assets} companyName={me.tenant.name} />;
}

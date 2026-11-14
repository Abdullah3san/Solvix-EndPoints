import type { Metadata } from "next";
import { serverApi } from "@/lib/api/server";
import { adminOr404 } from "@/lib/api/require-admin";
import type { AssetSettingsDto } from "@/lib/api/types";
import { PageHeader } from "@/components/ui/page-header";
import { AssetSettingsForm } from "@/components/features/assets/asset-settings-form";

export const metadata: Metadata = { title: "Asset management settings" };

export default async function AssetSettingsPage() {
  const settings = await adminOr404(serverApi<AssetSettingsDto>("asset-settings"));
  return (
    <>
      <PageHeader title="Asset management" description="Numbering, defaults and how managed endpoints are associated with assets." />
      <AssetSettingsForm initial={settings} />
    </>
  );
}

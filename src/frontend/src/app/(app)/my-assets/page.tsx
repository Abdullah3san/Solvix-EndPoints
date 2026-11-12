import type { Metadata } from "next";
import { serverApi } from "@/lib/api/server";
import type { MyAssetDto } from "@/lib/api/types";
import { categoryLabel, formatDay } from "@/lib/assets";
import { formatDate } from "@/lib/utils";
import { Card } from "@/components/ui/card";
import { PageHeader } from "@/components/ui/page-header";
import { Table, THead, TBody, TR, TH, TD, EmptyRow } from "@/components/ui/table";
import { AssetStatusBadge } from "@/components/features/assets/asset-badges";

export const metadata: Metadata = { title: "My assets" };

export default async function MyAssetsPage() {
  const assets = await serverApi<MyAssetDto[]>("my-assets");
  return (
    <>
      <PageHeader title="My assets" description="Equipment currently checked out to you. Contact IT if something is missing or damaged." />
      <Card>
        <Table>
          <THead>
            <TR>
              <TH>Asset tag</TH>
              <TH>Asset</TH>
              <TH>Serial number</TH>
              <TH>Status</TH>
              <TH>Since</TH>
              <TH>Warranty until</TH>
            </TR>
          </THead>
          <TBody>
            {assets.length === 0 ? (
              <EmptyRow colSpan={6}>No assets are assigned to you.</EmptyRow>
            ) : (
              assets.map((a) => (
                <TR key={a.id}>
                  <TD className="font-mono text-xs">{a.assetTag}</TD>
                  <TD>
                    <p className="font-medium">{a.name}</p>
                    <p className="text-xs text-muted-foreground">
                      {categoryLabel(a.category)} · {[a.manufacturer, a.model].filter(Boolean).join(" ") || "—"}
                    </p>
                  </TD>
                  <TD className="font-mono text-xs">{a.serialNumber ?? "—"}</TD>
                  <TD>
                    <AssetStatusBadge status={a.status} />
                  </TD>
                  <TD className="text-muted-foreground">{formatDate(a.assignedAtUtc)}</TD>
                  <TD className="text-muted-foreground">{formatDay(a.warrantyEndDate)}</TD>
                </TR>
              ))
            )}
          </TBody>
        </Table>
      </Card>
    </>
  );
}

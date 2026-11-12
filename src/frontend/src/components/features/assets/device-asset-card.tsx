"use client";

import * as React from "react";
import Link from "next/link";
import { api } from "@/lib/api/client";
import type { DeviceAssetDto } from "@/lib/api/types";
import { categoryLabel } from "@/lib/assets";
import { messageFrom } from "@/lib/forms";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { AssetStatusBadge, ConfidenceBadge } from "./asset-badges";

/** Physical asset associated with a managed endpoint (shown on the device page to administrators). */
export function DeviceAssetCard({ deviceId }: { deviceId: string }) {
  const [data, setData] = React.useState<DeviceAssetDto | null>(null);
  const [error, setError] = React.useState<string | null>(null);

  React.useEffect(() => {
    let cancelled = false;
    api
      .get<DeviceAssetDto>(`asset-matches/devices/${deviceId}`)
      .then((d) => !cancelled && setData(d))
      .catch((e) => !cancelled && setError(messageFrom(e)));
    return () => {
      cancelled = true;
    };
  }, [deviceId]);

  const approved = data?.approved;
  return (
    <Card>
      <CardHeader>
        <CardTitle>Physical asset</CardTitle>
        <CardDescription>The asset register record for this hardware. The two records are kept separately.</CardDescription>
      </CardHeader>
      <CardContent className="space-y-2 text-sm">
        {error ? (
          <p className="text-muted-foreground">{error}</p>
        ) : !data ? (
          <p className="text-muted-foreground">Loading…</p>
        ) : approved ? (
          <div className="flex flex-wrap items-center gap-2">
            <Link href={`/assets/${approved.asset.id}`} className="font-medium hover:underline">
              {approved.asset.assetTag} · {approved.asset.name}
            </Link>
            <AssetStatusBadge status={approved.asset.status} />
            <span className="text-muted-foreground">{categoryLabel(approved.asset.category)}</span>
          </div>
        ) : data.suggestions.length ? (
          <>
            <p>Possible matches waiting for review:</p>
            <ul className="space-y-1">
              {data.suggestions.map((s) => (
                <li key={s.id} className="flex flex-wrap items-center gap-2">
                  <Link href={`/assets/${s.asset.id}`} className="hover:underline">
                    {s.asset.assetTag} · {s.asset.name}
                  </Link>
                  <ConfidenceBadge confidence={s.confidence} />
                </li>
              ))}
            </ul>
            <Link href="/assets/matching" className="text-primary underline">
              Review matches
            </Link>
          </>
        ) : (
          <p className="text-muted-foreground">
            No asset is associated.{" "}
            <Link href="/assets/matching" className="text-primary underline">
              Register it as an asset
            </Link>{" "}
            or link it from an existing asset.
          </p>
        )}
      </CardContent>
    </Card>
  );
}

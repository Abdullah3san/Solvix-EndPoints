"use client";

import * as React from "react";
import Link from "next/link";
import { api } from "@/lib/api/client";
import type { ComplianceReport, PagedResult, SoftwareCatalogEntryDto } from "@/lib/api/types";
import { Badge } from "@/components/ui/badge";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { Table, THead, TBody, TR, TH, TD, EmptyRow } from "@/components/ui/table";

interface Installation {
  deviceId: string;
  deviceName: string;
  version: string | null;
}

export function InventoryView({ initial, compliance }: { initial: SoftwareCatalogEntryDto[]; compliance: ComplianceReport }) {
  const [items, setItems] = React.useState(initial);
  const [search, setSearch] = React.useState("");
  const [selected, setSelected] = React.useState<SoftwareCatalogEntryDto | null>(null);
  const [installations, setInstallations] = React.useState<Installation[]>([]);

  React.useEffect(() => {
    let cancelled = false;
    const timer = setTimeout(() => {
      api.get<PagedResult<SoftwareCatalogEntryDto>>("software/inventory/catalog", { search, pageSize: 50 }).then((r) => !cancelled && setItems(r.items), () => undefined);
    }, 250);
    return () => {
      cancelled = true;
      clearTimeout(timer);
    };
  }, [search]);

  async function select(entry: SoftwareCatalogEntryDto) {
    setSelected(entry);
    setInstallations(await api.get<Installation[]>("software/inventory/installations", { name: entry.name }));
  }

  return (
    <div className="space-y-4">
      <Card>
        <CardHeader><CardTitle>Restriction compliance</CardTitle></CardHeader>
        <CardContent className="space-y-2 text-sm">
          <p>{compliance.compliantDevices} of {compliance.devicesEvaluated} devices comply with all enabled rules.</p>
          {compliance.violations.slice(0, 20).map((v, i) => (
            <p key={i}>
              <Badge tone={v.ruleType === "Blocked" ? "danger" : "warning"}>{v.ruleName}</Badge>{" "}
              <Link className="text-primary hover:underline" href={`/endpoints/devices/${v.deviceId}`}>{v.deviceName}</Link> — {v.problem}{v.applicationName ? ` (${v.applicationName} ${v.installedVersion ?? ""})` : ""}
            </p>
          ))}
        </CardContent>
      </Card>

      <Input aria-label="Search software" placeholder="Search applications or publishers…" value={search} onChange={(e) => setSearch(e.target.value)} className="max-w-md" />
      <div className="grid gap-4 lg:grid-cols-[2fr_1fr]">
        <Card>
          <Table>
            <THead><TR><TH>Application</TH><TH>Publisher</TH><TH>Devices</TH><TH>Versions</TH></TR></THead>
            <TBody>
              {items.length === 0 ? <EmptyRow colSpan={4}>No software reported yet.</EmptyRow> : items.map((e) => (
                <TR key={e.name}>
                  <TD><button className="text-left font-medium text-primary hover:underline" onClick={() => void select(e)}>{e.name}</button></TD>
                  <TD className="text-muted-foreground">{e.publisher ?? "—"}</TD>
                  <TD>{e.deviceCount}</TD>
                  <TD className="text-xs">{e.versions.slice(0, 4).join(", ")}{e.versions.length > 4 ? "…" : ""}</TD>
                </TR>
              ))}
            </TBody>
          </Table>
        </Card>
        {selected ? (
          <Card>
            <CardHeader><CardTitle>{selected.name}</CardTitle></CardHeader>
            <CardContent className="space-y-1 text-sm">
              {installations.map((i) => (
                <p key={`${i.deviceId}${i.version}`}><Link className="text-primary hover:underline" href={`/endpoints/devices/${i.deviceId}`}>{i.deviceName}</Link> · {i.version ?? "unknown"}</p>
              ))}
            </CardContent>
          </Card>
        ) : null}
      </div>
    </div>
  );
}

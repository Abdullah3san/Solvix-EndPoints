"use client";

import * as React from "react";
import Link from "next/link";
import { useRouter } from "next/navigation";
import { Archive, ArrowLeftRight, CheckCircle2, Link2, LogIn, MoreHorizontal, Pencil, Printer, RotateCcw, SearchX, Trash2, UserPlus, Wrench } from "lucide-react";
import { api, PROXY_PREFIX } from "@/lib/api/client";
import type {
  AssetDetailsDto,
  AssignmentDto,
  AuditLogEntryDto,
  BranchDto,
  DepartmentDto,
  MaintenanceRecordDto,
  TenantUserDto,
  PagedResult,
  TransferDto,
  VendorDto,
} from "@/lib/api/types";
import { assigneeLabel, auditActionLabel, categoryLabel, formatDay, formatMoney, maintenanceTone, MAINTENANCE_TYPES, DISPOSAL_METHODS } from "@/lib/assets";
import { connectivityTone, formatRelative } from "@/lib/devices";
import { messageFrom } from "@/lib/forms";
import { formatDate } from "@/lib/utils";
import { Alert } from "@/components/ui/alert";
import { Badge } from "@/components/ui/badge";
import { Button, buttonClasses } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Menu, MenuPanel, MenuItem, MenuTrigger } from "@/components/ui/menu";
import { PageHeader } from "@/components/ui/page-header";
import { Table, THead, TBody, TR, TH, TD, EmptyRow } from "@/components/ui/table";
import { useToast } from "@/components/providers/toast-provider";
import { AssetStatusBadge, ConfidenceBadge, EndpointLinkBadge, InfoRows, WarrantyBadge } from "./asset-badges";
import {
  AssignDialog,
  DisposeDialog,
  EditAssetDialog,
  LinkEndpointDialog,
  MaintenanceDialog,
  RetireDialog,
  ReturnDialog,
  TransferDialog,
  UnlinkDialog,
  type AssetDialog,
} from "./asset-dialogs";

const tabs = ["Overview", "Assignments", "Maintenance", "Transfers", "Depreciation", "History"] as const;
type Tab = (typeof tabs)[number];

interface Props {
  initial: AssetDetailsDto;
  members: TenantUserDto[];
  branches: BranchDto[];
  departments: DepartmentDto[];
  vendors: VendorDto[];
}

export function AssetDetailsView({ initial, members, branches, departments, vendors }: Props) {
  const router = useRouter();
  const { toast } = useToast();
  const [asset, setAsset] = React.useState(initial);
  const [tab, setTab] = React.useState<Tab>("Overview");
  const [dialog, setDialog] = React.useState<AssetDialog>(null);
  const [revision, setRevision] = React.useState(0);
  const s = asset.summary;
  const disposed = s.status === "Disposed";
  const retired = s.status === "Retired";

  // A server refresh brings a new `initial`: adopt it (derived during render, no effect needed).
  const [seen, setSeen] = React.useState(initial);
  if (seen !== initial) {
    setSeen(initial);
    setAsset(initial);
  }

  const saved = React.useCallback(
    (next?: AssetDetailsDto) => {
      if (next) setAsset(next);
      setRevision((r) => r + 1);
      router.refresh();
    },
    [router],
  );

  async function quick(path: string, success: string) {
    try {
      saved(await api.post<AssetDetailsDto>(`assets/${s.id}/${path}`, {}));
      toast({ tone: "success", title: success });
    } catch (e) {
      toast({ tone: "error", title: "Action failed", description: messageFrom(e) });
    }
  }

  const canAssign = s.status === "InStock" || s.status === "InUse";

  return (
    <>
      <PageHeader
        title={s.name}
        description={[s.assetTag, categoryLabel(s.category), [s.manufacturer, s.model].filter(Boolean).join(" ")].filter(Boolean).join(" · ")}
        actions={
          disposed ? (
            <Link href={`/assets/labels?ids=${s.id}`} className={buttonClasses({ variant: "outline" })}>
              <Printer className="h-4 w-4" /> Label
            </Link>
          ) : (
            <>
              {canAssign ? (
                <Button variant="outline" onClick={() => setDialog("assign")}>
                  <UserPlus className="h-4 w-4" /> {s.assignee ? "Re-assign" : "Assign"}
                </Button>
              ) : null}
              {s.assignee ? (
                <Button variant="outline" onClick={() => setDialog("return")}>
                  <LogIn className="h-4 w-4" /> Check in
                </Button>
              ) : null}
              <Button variant="outline" onClick={() => setDialog("edit")}>
                <Pencil className="h-4 w-4" /> Edit
              </Button>
              <Menu>
                <MenuTrigger className={buttonClasses({ variant: "outline", size: "icon" })} aria-label="More actions">
                  <MoreHorizontal className="h-4 w-4" />
                </MenuTrigger>
                <MenuPanel align="end">
                  <MenuItem onSelect={() => setDialog("transfer")}>
                    <ArrowLeftRight className="h-4 w-4" /> Transfer
                  </MenuItem>
                  {!retired && s.status !== "InMaintenance" ? (
                    <MenuItem onSelect={() => setDialog("maintenance")}>
                      <Wrench className="h-4 w-4" /> Log maintenance
                    </MenuItem>
                  ) : null}
                  {s.status === "Missing" ? (
                    <MenuItem onSelect={() => quick("mark-found", "Asset found")}>
                      <CheckCircle2 className="h-4 w-4" /> Mark found
                    </MenuItem>
                  ) : s.status === "InStock" || s.status === "InUse" ? (
                    <MenuItem onSelect={() => quick("mark-missing", "Asset marked missing")}>
                      <SearchX className="h-4 w-4" /> Mark missing
                    </MenuItem>
                  ) : null}
                  <MenuItem onSelect={() => router.push(`/assets/labels?ids=${s.id}`)}>
                    <Printer className="h-4 w-4" /> Print label
                  </MenuItem>
                  {retired ? (
                    <>
                      <MenuItem onSelect={() => quick("reinstate", "Asset reinstated")}>
                        <RotateCcw className="h-4 w-4" /> Reinstate
                      </MenuItem>
                      <MenuItem onSelect={() => setDialog("dispose")} className="text-destructive">
                        <Trash2 className="h-4 w-4" /> Dispose…
                      </MenuItem>
                    </>
                  ) : s.status !== "InMaintenance" ? (
                    <MenuItem onSelect={() => setDialog("retire")} className="text-destructive">
                      <Archive className="h-4 w-4" /> Retire…
                    </MenuItem>
                  ) : null}
                </MenuPanel>
              </Menu>
            </>
          )
        }
      />

      <div className="mb-4 flex flex-wrap items-center gap-2">
        <AssetStatusBadge status={s.status} />
        <Badge>{s.condition}</Badge>
        {s.warranty !== "None" ? <WarrantyBadge state={s.warranty} /> : null}
        <EndpointLinkBadge state={s.endpointLink} />
        <span className="text-sm text-muted-foreground">Updated {formatRelative(s.updatedAtUtc)}</span>
      </div>

      {disposed ? (
        <Alert className="mb-4" title={`Disposed ${formatDate(asset.disposedAtUtc)}`}>
          {DISPOSAL_METHODS.find((m) => m.value === asset.disposalMethod)?.label}
          {asset.disposalReference ? ` · reference ${asset.disposalReference}` : ""}. The record is read-only.
        </Alert>
      ) : retired ? (
        <Alert className="mb-4" title={`Retired ${formatDate(asset.retiredAtUtc)}`}>
          {asset.retirementReason}
        </Alert>
      ) : asset.openMaintenance ? (
        <Alert className="mb-4" title={`${asset.openMaintenance.status === "InProgress" ? "In maintenance" : "Maintenance scheduled"}: ${asset.openMaintenance.title}`}>
          <Link href="/assets/maintenance" className="underline">
            Manage maintenance
          </Link>
        </Alert>
      ) : null}

      <div role="tablist" aria-label="Asset sections" className="mb-4 flex gap-1 overflow-x-auto border-b border-border">
        {tabs.map((t) => (
          <button
            key={t}
            role="tab"
            aria-selected={tab === t}
            onClick={() => setTab(t)}
            className={`-mb-px whitespace-nowrap border-b-2 px-3 py-2 text-sm font-medium ${tab === t ? "border-primary text-foreground" : "border-transparent text-muted-foreground hover:text-foreground"}`}
          >
            {t}
          </button>
        ))}
      </div>

      {tab === "Overview" ? <Overview asset={asset} onLink={() => setDialog("link")} onUnlink={() => setDialog("unlink")} /> : null}
      {tab === "Assignments" ? <AssignmentsTab assetId={s.id} revision={revision} /> : null}
      {tab === "Maintenance" ? <MaintenanceTab assetId={s.id} revision={revision} /> : null}
      {tab === "Transfers" ? <TransfersTab assetId={s.id} revision={revision} /> : null}
      {tab === "Depreciation" ? <DepreciationTab asset={asset} /> : null}
      {tab === "History" ? <HistoryTab assetId={s.id} revision={revision} /> : null}

      {dialog === "edit" ? <EditAssetDialog asset={asset} onClose={() => setDialog(null)} onSaved={saved} branches={branches} departments={departments} vendors={vendors} /> : null}
      {dialog === "assign" ? <AssignDialog asset={asset} onClose={() => setDialog(null)} onSaved={saved} members={members} /> : null}
      {dialog === "return" ? <ReturnDialog asset={asset} onClose={() => setDialog(null)} onSaved={saved} /> : null}
      {dialog === "transfer" ? <TransferDialog asset={asset} onClose={() => setDialog(null)} onSaved={saved} branches={branches} departments={departments} /> : null}
      {dialog === "maintenance" ? <MaintenanceDialog asset={asset} onClose={() => setDialog(null)} onSaved={() => saved()} vendors={vendors} /> : null}
      {dialog === "retire" ? <RetireDialog asset={asset} onClose={() => setDialog(null)} onSaved={saved} /> : null}
      {dialog === "dispose" ? <DisposeDialog asset={asset} onClose={() => setDialog(null)} onSaved={saved} /> : null}
      {dialog === "link" ? <LinkEndpointDialog asset={asset} onClose={() => setDialog(null)} onSaved={() => saved()} /> : null}
      {dialog === "unlink" ? <UnlinkDialog asset={asset} onClose={() => setDialog(null)} onSaved={() => saved()} /> : null}
    </>
  );
}

function Overview({ asset, onLink, onUnlink }: { asset: AssetDetailsDto; onLink: () => void; onUnlink: () => void }) {
  const s = asset.summary;
  const link = asset.endpointLink;
  return (
    <div className="grid gap-4 lg:grid-cols-2 [&>*]:min-w-0">
      <Card>
        <CardHeader>
          <CardTitle>Asset</CardTitle>
        </CardHeader>
        <CardContent>
          <InfoRows
            rows={[
              ["Asset tag", <span key="t" className="font-mono">{s.assetTag}</span>],
              ["Category", categoryLabel(s.category)],
              ["Manufacturer", s.manufacturer],
              ["Model", s.model],
              ["Serial number", s.serialNumber ? <span key="s" className="font-mono">{s.serialNumber}</span> : null],
              ["Condition", s.condition],
              ["Last physical audit", s.lastAuditedAtUtc ? formatDate(s.lastAuditedAtUtc) : "Never"],
              ["Notes", asset.description],
            ]}
          />
        </CardContent>
      </Card>
      <Card>
        <CardHeader>
          <CardTitle>Holder and location</CardTitle>
        </CardHeader>
        <CardContent>
          <InfoRows
            rows={[
              ["Assigned to", s.assignee ? `${assigneeLabel(s.assignee)}${s.assignee.email ? ` · ${s.assignee.email}` : ""}${s.assignee.userId && !s.assignee.isMember ? " (no longer a member)" : ""}` : "Not assigned"],
              ["Since", asset.assignedAtUtc ? formatDate(asset.assignedAtUtc) : null],
              ["Branch", s.branch?.name],
              ["Department", s.department?.name],
              ["Location", s.location],
            ]}
          />
        </CardContent>
      </Card>
      <Card>
        <CardHeader>
          <CardTitle>Purchase and warranty</CardTitle>
        </CardHeader>
        <CardContent>
          <InfoRows
            rows={[
              ["Vendor", s.vendor?.name],
              ["Purchased", formatDay(s.purchaseDate)],
              ["Cost", formatMoney(s.purchaseCost, s.currency)],
              ["PO / invoice", [asset.purchaseOrderNumber, asset.invoiceNumber].filter(Boolean).join(" · ")],
              ["Warranty", s.warrantyEndDate ? `${formatDay(asset.warrantyStartDate)} – ${formatDay(s.warrantyEndDate)}` : null],
              ["Warranty provider", [asset.warrantyProvider, asset.warrantyReference].filter(Boolean).join(" · ")],
              ["Book value", asset.depreciation ? formatMoney(asset.depreciation.bookValue, s.currency) : null],
            ]}
          />
        </CardContent>
      </Card>
      <Card>
        <CardHeader className="flex-row items-start justify-between">
          <div>
            <CardTitle>Managed endpoint</CardTitle>
            <CardDescription>
              {asset.endpointManageable ? "The Windows agent identity associated with this hardware." : "This category does not run the agent; linking is optional."}
            </CardDescription>
          </div>
          {link ? (
            s.status !== "Disposed" ? (
              <Button size="sm" variant="outline" onClick={onUnlink}>
                Remove
              </Button>
            ) : null
          ) : s.status !== "Disposed" ? (
            <Button size="sm" variant="outline" onClick={onLink}>
              <Link2 className="h-4 w-4" /> Link
            </Button>
          ) : null}
        </CardHeader>
        <CardContent className="space-y-3 text-sm">
          {link ? (
            <>
              <div className="flex flex-wrap items-center gap-2">
                <Link href={`/endpoints/devices/${link.endpoint.id}`} className="font-medium hover:underline">
                  {link.endpoint.displayName ?? link.endpoint.hostname}
                </Link>
                <Badge tone={connectivityTone(link.endpoint.isActive ? "Active" : "Retired", link.endpoint.connectivity)}>
                  {link.endpoint.isActive ? link.endpoint.connectivity : "Retired"}
                </Badge>
                <ConfidenceBadge confidence={link.confidence} />
                <Badge>{link.source === "Manual" ? "Linked manually" : "Matched"}</Badge>
              </div>
              <InfoRows
                rows={[
                  ["Operating system", link.endpoint.osName],
                  ["Reported hardware", [link.endpoint.manufacturer, link.endpoint.model].filter(Boolean).join(" ")],
                  ["Reported serial", link.endpoint.serialNumber],
                  ["Last seen", formatRelative(link.endpoint.lastSeenAtUtc)],
                  ["Associated", formatDate(link.approvedAtUtc)],
                ]}
              />
              {link.discrepancies.length ? (
                <Alert title="Records differ">
                  <ul className="list-inside list-disc">
                    {link.discrepancies.map((d) => (
                      <li key={d.field}>
                        {d.field}: asset “{d.asset ?? "—"}”, endpoint “{d.endpoint ?? "—"}”
                      </li>
                    ))}
                  </ul>
                </Alert>
              ) : null}
            </>
          ) : asset.pendingSuggestions > 0 ? (
            <Alert title={`${asset.pendingSuggestions} suggested match${asset.pendingSuggestions === 1 ? "" : "es"}`}>
              <Link href={`/assets/matching?assetId=${s.id}`} className="underline">
                Review in Endpoint matching
              </Link>
            </Alert>
          ) : (
            <p className="text-muted-foreground">
              {asset.endpointManageable
                ? "No managed endpoint is associated. When the agent enrolls on this hardware, it is suggested here automatically (by serial number and manufacturer)."
                : "Switches, printers, monitors and similar equipment are tracked without an agent."}
            </p>
          )}
        </CardContent>
      </Card>
      <Card className="lg:col-span-2">
        <CardHeader className="flex-row items-start justify-between">
          <div>
            <CardTitle>Label</CardTitle>
            <CardDescription>Code 128 barcode of the asset tag and a QR code ({asset.qrPayload}).</CardDescription>
          </div>
          <Link href={`/assets/labels?ids=${s.id}`} className={buttonClasses({ variant: "outline", size: "sm" })}>
            <Printer className="h-4 w-4" /> Print
          </Link>
        </CardHeader>
        <CardContent className="flex flex-wrap items-center gap-6">
          {/* eslint-disable-next-line @next/next/no-img-element -- SVG generated by the API */}
          <img src={`${PROXY_PREFIX}/assets/${s.id}/barcode.svg`} alt={`Barcode ${s.assetTag}`} className="h-20 max-w-full rounded border border-border bg-white p-1" />
          {/* eslint-disable-next-line @next/next/no-img-element -- SVG generated by the API */}
          <img src={`${PROXY_PREFIX}/assets/${s.id}/qr.svg`} alt="QR code" className="h-24 w-24 rounded border border-border bg-white p-1" />
        </CardContent>
      </Card>
    </div>
  );
}

function useTabData<T>(path: string, revision: number) {
  const { toast } = useToast();
  const [data, setData] = React.useState<T | null>(null);
  React.useEffect(() => {
    let cancelled = false;
    api
      .get<T>(path)
      .then((d) => !cancelled && setData(d))
      .catch((e) => !cancelled && toast({ tone: "error", title: "Could not load", description: messageFrom(e) }));
    return () => {
      cancelled = true;
    };
  }, [path, revision, toast]);
  return data;
}

function AssignmentsTab({ assetId, revision }: { assetId: string; revision: number }) {
  const rows = useTabData<AssignmentDto[]>(`assets/${assetId}/assignments`, revision);
  return (
    <Card>
      <Table>
        <THead>
          <TR>
            <TH>Assigned to</TH>
            <TH>Branch / department</TH>
            <TH>From</TH>
            <TH>Until</TH>
            <TH>Return condition</TH>
            <TH>Notes</TH>
          </TR>
        </THead>
        <TBody>
          {rows === null ? (
            <EmptyRow colSpan={6}>Loading…</EmptyRow>
          ) : rows.length === 0 ? (
            <EmptyRow colSpan={6}>This asset has never been assigned.</EmptyRow>
          ) : (
            rows.map((r) => (
              <TR key={r.id}>
                <TD className="font-medium">
                  {assigneeLabel(r.assignee)}
                  {!r.assignee.userId ? <Badge className="ml-1">no login</Badge> : null}
                </TD>
                <TD className="text-muted-foreground">{[r.branch?.name, r.department?.name].filter(Boolean).join(" · ") || "—"}</TD>
                <TD className="whitespace-nowrap">
                  {formatDate(r.assignedAtUtc)}
                  {r.assignedBy?.name ? <p className="text-xs text-muted-foreground">by {r.assignedBy.name}</p> : null}
                </TD>
                <TD className="whitespace-nowrap">
                  {r.returnedAtUtc ? formatDate(r.returnedAtUtc) : <Badge tone="success">Current</Badge>}
                  {!r.returnedAtUtc && r.expectedReturnDate ? <p className="text-xs text-muted-foreground">due {formatDay(r.expectedReturnDate)}</p> : null}
                </TD>
                <TD>{r.returnCondition ?? "—"}</TD>
                <TD className="max-w-64 text-muted-foreground">{[r.notes, r.returnNotes].filter(Boolean).join(" · ") || "—"}</TD>
              </TR>
            ))
          )}
        </TBody>
      </Table>
    </Card>
  );
}

function MaintenanceTab({ assetId, revision }: { assetId: string; revision: number }) {
  const page = useTabData<PagedResult<MaintenanceRecordDto>>(`assets/${assetId}/maintenance`, revision);
  if (!page) return <p className="text-sm text-muted-foreground">Loading…</p>;
  if (page.items.length === 0) return <p className="text-sm text-muted-foreground">No maintenance has been logged for this asset.</p>;
  return (
    <ol className="relative ml-3 space-y-4 border-l border-border pl-6">
      {page.items.map((m) => (
        <li key={m.id} className="relative">
          <span className="absolute -left-[31px] top-1.5 h-3 w-3 rounded-full border-2 border-card bg-primary" aria-hidden />
          <Card>
            <CardContent className="space-y-1 p-4 text-sm">
              <div className="flex flex-wrap items-center gap-2">
                <span className="font-medium">{m.title}</span>
                <Badge tone={maintenanceTone(m.status)}>{m.status === "InProgress" ? "In progress" : m.status}</Badge>
                <Badge>{MAINTENANCE_TYPES.find((t) => t.value === m.type)?.label ?? m.type}</Badge>
                {m.cost !== null ? <span className="ml-auto tabular-nums">{formatMoney(m.cost)}</span> : null}
              </div>
              <p className="text-xs text-muted-foreground">
                {m.completedAtUtc ? `Closed ${formatDate(m.completedAtUtc)}` : m.startedAtUtc ? `Started ${formatDate(m.startedAtUtc)}` : m.scheduledFor ? `Scheduled for ${formatDay(m.scheduledFor)}` : `Logged ${formatDate(m.createdAtUtc)}`}
                {m.vendor?.name ? ` · ${m.vendor.name}` : ""}
                {m.performedBy ? ` · ${m.performedBy}` : ""}
                {m.createdBy?.name ? ` · logged by ${m.createdBy.name}` : ""}
              </p>
              {m.description ? <p>{m.description}</p> : null}
              {m.resolution ? <p className="text-muted-foreground">Resolution: {m.resolution}</p> : null}
            </CardContent>
          </Card>
        </li>
      ))}
    </ol>
  );
}

function TransfersTab({ assetId, revision }: { assetId: string; revision: number }) {
  const rows = useTabData<TransferDto[]>(`assets/${assetId}/transfers`, revision);
  const place = (b: { name: string | null } | null, d: { name: string | null } | null, l: string | null) => [b?.name, d?.name, l].filter(Boolean).join(" · ") || "—";
  return (
    <Card>
      <Table>
        <THead>
          <TR>
            <TH>When</TH>
            <TH>From</TH>
            <TH>To</TH>
            <TH>By</TH>
            <TH>Reason</TH>
          </TR>
        </THead>
        <TBody>
          {rows === null ? (
            <EmptyRow colSpan={5}>Loading…</EmptyRow>
          ) : rows.length === 0 ? (
            <EmptyRow colSpan={5}>No transfers yet.</EmptyRow>
          ) : (
            rows.map((t) => (
              <TR key={t.id}>
                <TD className="whitespace-nowrap">{formatDate(t.transferredAtUtc)}</TD>
                <TD className="text-muted-foreground">{place(t.fromBranch, t.fromDepartment, t.fromLocation)}</TD>
                <TD>{place(t.toBranch, t.toDepartment, t.toLocation)}</TD>
                <TD className="text-muted-foreground">{t.transferredBy?.name ?? "—"}</TD>
                <TD className="max-w-64 text-muted-foreground">{t.reason ?? "—"}</TD>
              </TR>
            ))
          )}
        </TBody>
      </Table>
    </Card>
  );
}

function DepreciationTab({ asset }: { asset: AssetDetailsDto }) {
  const d = asset.depreciation;
  const currency = asset.summary.currency;
  if (!d) {
    return <Alert title="Not depreciated">Set a depreciation method, useful life, purchase date and cost (Edit) to calculate book value.</Alert>;
  }
  return (
    <div className="grid gap-4 lg:grid-cols-3">
      <Card>
        <CardHeader>
          <CardTitle>Book value today</CardTitle>
        </CardHeader>
        <CardContent>
          <p className="text-2xl font-semibold tabular-nums">{formatMoney(d.bookValue, currency)}</p>
          <InfoRows
            rows={[
              ["Method", d.method === "StraightLine" ? "Straight-line" : "Double-declining balance"],
              ["Cost", formatMoney(d.cost, currency)],
              ["Salvage value", formatMoney(d.salvageValue, currency)],
              ["Accumulated", formatMoney(d.accumulatedDepreciation, currency)],
              ["Months elapsed", `${d.monthsElapsed} of ${d.usefulLifeMonths}`],
              ["Monthly charge", formatMoney(d.currentMonthlyCharge, currency)],
              ["Fully depreciated", formatDay(d.fullyDepreciatedOn)],
            ]}
          />
        </CardContent>
      </Card>
      <Card className="lg:col-span-2">
        <CardHeader>
          <CardTitle>Schedule by year</CardTitle>
        </CardHeader>
        <Table>
          <THead>
            <TR>
              <TH>Year</TH>
              <TH className="text-right">Opening</TH>
              <TH className="text-right">Depreciation</TH>
              <TH className="text-right">Closing</TH>
            </TR>
          </THead>
          <TBody>
            {d.schedule.map((y) => (
              <TR key={y.year}>
                <TD>{y.year}</TD>
                <TD className="text-right tabular-nums">{formatMoney(y.openingValue, currency)}</TD>
                <TD className="text-right tabular-nums">{formatMoney(y.depreciation, currency)}</TD>
                <TD className="text-right tabular-nums">{formatMoney(y.closingValue, currency)}</TD>
              </TR>
            ))}
          </TBody>
        </Table>
      </Card>
    </div>
  );
}

function HistoryTab({ assetId, revision }: { assetId: string; revision: number }) {
  const page = useTabData<PagedResult<AuditLogEntryDto>>(`assets/${assetId}/history?pageSize=200`, revision);
  if (!page) return <p className="text-sm text-muted-foreground">Loading…</p>;
  return <AuditLogList entries={page.items} />;
}

export function AuditLogList({ entries }: { entries: AuditLogEntryDto[] }) {
  if (entries.length === 0) return <p className="text-sm text-muted-foreground">No activity recorded.</p>;
  return (
    <Card>
      <ul className="divide-y divide-border">
        {entries.map((e) => (
          <li key={e.id} className="space-y-1 p-3 text-sm">
            <div className="flex flex-wrap items-center gap-2">
              <span className="font-medium">{auditActionLabel(e.action)}</span>
              <span className="text-muted-foreground">{e.summary}</span>
              <span className="ml-auto whitespace-nowrap text-xs text-muted-foreground" title={e.correlationId ?? undefined}>
                {formatDate(e.occurredAtUtc)} · {e.actorKind === "System" ? "System" : (e.actor?.name ?? "Former member")}
              </span>
            </div>
            {e.changes ? (
              <ul className="text-xs text-muted-foreground">
                {Object.entries(e.changes).map(([field, change]) => (
                  <li key={field}>
                    <span className="font-medium text-foreground">{field}</span>: {change.from ?? "—"} → {change.to ?? "—"}
                  </li>
                ))}
              </ul>
            ) : null}
          </li>
        ))}
      </ul>
    </Card>
  );
}

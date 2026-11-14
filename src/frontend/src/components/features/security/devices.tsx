"use client";

import * as React from "react";
import Link from "next/link";
import { Copy, Plus } from "lucide-react";
import { api } from "@/lib/api/client";
import type { ComplianceBand, Enrollment, EndpointSummary } from "@/lib/api/security-types";
import type { PagedResult } from "@/lib/api/types";
import { fieldErrorsFrom, messageFrom } from "@/lib/forms";
import { bandLabel, timeAgo } from "@/lib/security";
import { Alert } from "@/components/ui/alert";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card } from "@/components/ui/card";
import { Dialog } from "@/components/ui/dialog";
import { Field } from "@/components/ui/field";
import { Input, Select } from "@/components/ui/input";
import { EmptyRow, Table, TBody, TD, TH, THead, TR } from "@/components/ui/table";
import { useToast } from "@/components/providers/toast-provider";
import { useCan } from "@/components/providers/session-provider";
import { LoadState, ScoreBadge, useResource } from "./security-ui";
import { Pager } from "./shared";

export function DevicesView() {
  const { isAdmin } = useCan();
  const { toast } = useToast();
  const [search, setSearch] = React.useState("");
  const [applied, setApplied] = React.useState("");
  const [band, setBand] = React.useState<ComplianceBand | "">("");
  const [page, setPage] = React.useState(1);
  const resource = useResource<PagedResult<EndpointSummary>>("security/endpoints", { search: applied, band, page, pageSize: 25 });
  const [enrolling, setEnrolling] = React.useState(false);
  const [issued, setIssued] = React.useState<Enrollment | null>(null);

  return (
    <>
      <div className="mb-4 flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
        <form
          className="flex flex-1 flex-wrap items-center gap-2"
          onSubmit={(e) => {
            e.preventDefault();
            setPage(1);
            setApplied(search);
          }}
        >
          <Input placeholder="Search hostname…" value={search} onChange={(e) => setSearch(e.target.value)} className="max-w-xs" aria-label="Search devices" />
          <Select
            value={band}
            onChange={(e) => {
              setBand(e.target.value as ComplianceBand | "");
              setPage(1);
            }}
            className="w-44"
            aria-label="Filter by compliance"
          >
            <option value="">All compliance states</option>
            {(["Compliant", "AtRisk", "NonCompliant", "Unknown"] as ComplianceBand[]).map((b) => (
              <option key={b} value={b}>
                {bandLabel(b)}
              </option>
            ))}
          </Select>
          <Button type="submit" variant="outline">
            Search
          </Button>
        </form>
        {isAdmin ? (
          <Button onClick={() => setEnrolling(true)}>
            <Plus className="h-4 w-4" /> Enrol device
          </Button>
        ) : null}
      </div>

      <Card>
        <LoadState resource={resource}>
          {(data) => (
            <>
              <Table>
                <THead>
                  <TR>
                    <TH>Device</TH>
                    <TH>Compliance</TH>
                    <TH>Open findings</TH>
                    <TH>Last report</TH>
                    <TH>Operating system</TH>
                  </TR>
                </THead>
                <TBody>
                  {data.items.length === 0 ? (
                    <EmptyRow colSpan={5}>No devices match.</EmptyRow>
                  ) : (
                    data.items.map((d) => (
                      <TR key={d.id}>
                        <TD className="font-medium">
                          <Link href={`/security/devices/${d.id}`} className="hover:underline">
                            {d.hostname}
                          </Link>
                          {d.status === "Revoked" ? <Badge className="ml-2">Revoked</Badge> : null}
                        </TD>
                        <TD>
                          <ScoreBadge score={d.score} band={d.band} />
                        </TD>
                        <TD className="text-xs tabular-nums text-muted-foreground">
                          {d.openCritical} critical · {d.openHigh} high · {d.openMedium} medium · {d.openLow} low
                        </TD>
                        <TD className="text-muted-foreground">
                          {timeAgo(d.lastReportAtUtc)}
                          {d.isStale ? <Badge tone="warning" className="ml-2">Stale</Badge> : null}
                        </TD>
                        <TD className="text-muted-foreground">{d.osCaption ?? "—"}</TD>
                      </TR>
                    ))
                  )}
                </TBody>
              </Table>
              <Pager data={data} onPage={setPage} />
            </>
          )}
        </LoadState>
      </Card>

      <EnrollDialog
        open={enrolling}
        onClose={() => setEnrolling(false)}
        onEnrolled={(e) => {
          setEnrolling(false);
          setIssued(e);
          void resource.reload();
        }}
      />
      <Dialog
        open={issued !== null}
        onClose={() => setIssued(null)}
        title="Device key"
        description={issued?.notice}
        footer={<Button onClick={() => setIssued(null)}>I have stored the key</Button>}
      >
        <div className="space-y-3">
          <Alert tone="error" title="Shown once">
            Solvix stores only a hash of this key. If you close this dialog without saving it, rotate the key from the device page.
          </Alert>
          <code className="block break-all rounded-md border border-border bg-muted p-3 text-xs">{issued?.deviceKey}</code>
          <Button
            variant="outline"
            onClick={async () => {
              if (issued) {
                await navigator.clipboard.writeText(issued.deviceKey);
                toast({ tone: "success", title: "Copied" });
              }
            }}
          >
            <Copy className="h-4 w-4" /> Copy key
          </Button>
        </div>
      </Dialog>
    </>
  );
}

function EnrollDialog({ open, onClose, onEnrolled }: { open: boolean; onClose: () => void; onEnrolled: (e: Enrollment) => void }) {
  const [hostname, setHostname] = React.useState("");
  const [busy, setBusy] = React.useState(false);
  const [errors, setErrors] = React.useState<Record<string, string>>({});
  const [message, setMessage] = React.useState<string | null>(null);

  async function submit() {
    setBusy(true);
    setErrors({});
    setMessage(null);
    try {
      const result = await api.post<Enrollment>("security/endpoints", { hostname });
      setHostname("");
      onEnrolled(result);
    } catch (e) {
      setErrors(fieldErrorsFrom(e));
      setMessage(messageFrom(e));
    } finally {
      setBusy(false);
    }
  }

  return (
    <Dialog
      open={open}
      onClose={onClose}
      title="Enrol a device"
      description="Creates the device and issues a one-time key for its agent."
      footer={
        <>
          <Button variant="outline" onClick={onClose}>
            Cancel
          </Button>
          <Button loading={busy} disabled={hostname.trim().length === 0} onClick={submit}>
            Enrol
          </Button>
        </>
      }
    >
      <div className="space-y-3">
        {message && Object.keys(errors).length === 0 ? <Alert tone="error">{message}</Alert> : null}
        <Field label="Hostname" htmlFor="enrol-hostname" error={errors.hostname} required>
          <Input id="enrol-hostname" value={hostname} onChange={(e) => setHostname(e.target.value)} maxLength={200} autoFocus />
        </Field>
      </div>
    </Dialog>
  );
}

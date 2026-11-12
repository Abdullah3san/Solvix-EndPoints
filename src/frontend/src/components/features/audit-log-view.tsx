"use client";

import * as React from "react";
import { api } from "@/lib/api/client";
import type { AuditEventDto, AuditOutcome, PagedResult } from "@/lib/api/types";
import { messageFrom } from "@/lib/forms";
import { formatDate } from "@/lib/utils";
import { Button } from "@/components/ui/button";
import { Badge } from "@/components/ui/badge";
import { Input, Select } from "@/components/ui/input";
import { Alert } from "@/components/ui/alert";
import { Card } from "@/components/ui/card";
import { Table, THead, TBody, TR, TH, TD, EmptyRow } from "@/components/ui/table";

const families = [
  { value: "", label: "All events" },
  { value: "login.", label: "Sign-in" },
  { value: "session.", label: "Sessions" },
  { value: "password.", label: "Passwords" },
  { value: "mfa.", label: "Multi-factor" },
  { value: "user.", label: "Users" },
  { value: "role.", label: "Roles" },
  { value: "invitation.", label: "Invitations" },
  { value: "account.locked", label: "Lockouts" },
  { value: "token.reuse_detected", label: "Token reuse" },
  { value: "security_policy.updated", label: "Policy changes" },
];

export function AuditLogView({ initial }: { initial: PagedResult<AuditEventDto> }) {
  const [data, setData] = React.useState(initial);
  const [eventType, setEventType] = React.useState("");
  const [outcome, setOutcome] = React.useState<"" | AuditOutcome>("");
  const [search, setSearch] = React.useState("");
  const [error, setError] = React.useState<string | null>(null);
  const [loading, setLoading] = React.useState(false);

  async function load(page: number) {
    setLoading(true);
    setError(null);
    try {
      setData(await api.get<PagedResult<AuditEventDto>>("security/audit-events", { page, pageSize: 50, eventType, outcome, search }));
    } catch (e) {
      setError(messageFrom(e));
    } finally {
      setLoading(false);
    }
  }

  return (
    <div className="space-y-4">
      <form
        className="flex flex-wrap items-end gap-2"
        onSubmit={(e) => {
          e.preventDefault();
          void load(1);
        }}
      >
        <Select aria-label="Event type" value={eventType} onChange={(e) => setEventType(e.target.value)} className="w-48">
          {families.map((f) => (
            <option key={f.value} value={f.value}>
              {f.label}
            </option>
          ))}
        </Select>
        <Select aria-label="Outcome" value={outcome} onChange={(e) => setOutcome(e.target.value as "" | AuditOutcome)} className="w-36">
          <option value="">Any outcome</option>
          <option value="Success">Success</option>
          <option value="Failure">Failure</option>
        </Select>
        <Input aria-label="Search e-mail or IP" placeholder="E-mail or IP address" value={search} onChange={(e) => setSearch(e.target.value)} className="w-56" />
        <Button type="submit" loading={loading}>
          Filter
        </Button>
      </form>
      {error ? <Alert tone="error">{error}</Alert> : null}
      <Card>
        <Table>
          <THead>
            <TR>
              <TH>When</TH>
              <TH>Event</TH>
              <TH>Account</TH>
              <TH>Details</TH>
              <TH>Origin</TH>
            </TR>
          </THead>
          <TBody>
            {data.items.length === 0 ? (
              <EmptyRow colSpan={5}>No events match.</EmptyRow>
            ) : (
              data.items.map((e) => (
                <TR key={e.id}>
                  <TD className="whitespace-nowrap text-muted-foreground">{formatDate(e.occurredAtUtc)}</TD>
                  <TD>
                    <div className="flex items-center gap-2">
                      <Badge tone={e.outcome === "Success" ? "success" : "danger"}>{e.outcome}</Badge>
                      <code className="text-xs">{e.eventType}</code>
                    </div>
                  </TD>
                  <TD className="text-sm">
                    {e.email ?? "—"}
                    {e.actorUserId && e.actorUserId !== e.userId ? <span className="block text-xs text-muted-foreground">by an administrator</span> : null}
                  </TD>
                  <TD className="max-w-xs truncate font-mono text-xs text-muted-foreground" title={e.detail ?? undefined}>
                    {e.detail ?? ""}
                  </TD>
                  <TD className="text-xs text-muted-foreground">
                    {e.ipAddress ?? "—"}
                    {e.userAgent ? <span className="block max-w-48 truncate" title={e.userAgent}>{e.userAgent}</span> : null}
                  </TD>
                </TR>
              ))
            )}
          </TBody>
        </Table>
      </Card>
      <div className="flex items-center justify-between text-sm text-muted-foreground">
        <span>
          Page {data.page} of {Math.max(1, data.totalPages)} · {data.totalCount} events
        </span>
        <div className="flex gap-2">
          <Button variant="outline" size="sm" disabled={data.page <= 1 || loading} onClick={() => load(data.page - 1)}>
            Previous
          </Button>
          <Button variant="outline" size="sm" disabled={data.page >= data.totalPages || loading} onClick={() => load(data.page + 1)}>
            Next
          </Button>
        </div>
      </div>
    </div>
  );
}

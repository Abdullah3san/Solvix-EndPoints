"use client";

import * as React from "react";
import { Plus } from "lucide-react";
import { api } from "@/lib/api/client";
import type { Advisory, Severity } from "@/lib/api/security-types";
import { emptyToNull, fieldErrorsFrom, messageFrom } from "@/lib/forms";
import { Alert } from "@/components/ui/alert";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card } from "@/components/ui/card";
import { Dialog } from "@/components/ui/dialog";
import { Field } from "@/components/ui/field";
import { Input, Select } from "@/components/ui/input";
import { EmptyRow, Table, TBody, TD, TH, THead, TR } from "@/components/ui/table";
import { useCan } from "@/components/providers/session-provider";
import { useToast } from "@/components/providers/toast-provider";
import { LoadState, SeverityBadge, useResource } from "./security-ui";

const empty = { title: "", productPattern: "", publisher: "", affectedFromVersion: "", fixedVersion: "", severity: "High" as Severity, reference: "" };

export function AdvisoriesView() {
  const { isAdmin } = useCan();
  const { toast } = useToast();
  const resource = useResource<Advisory[]>("security/advisories");
  const [editing, setEditing] = React.useState<Advisory | "new" | null>(null);
  const [draft, setDraft] = React.useState(empty);
  const [errors, setErrors] = React.useState<Record<string, string>>({});
  const [busy, setBusy] = React.useState(false);

  function open(a: Advisory | "new") {
    setErrors({});
    setDraft(a === "new" ? empty : { title: a.title, productPattern: a.productPattern, publisher: a.publisher ?? "", affectedFromVersion: a.affectedFromVersion ?? "", fixedVersion: a.fixedVersion, severity: a.severity, reference: a.reference ?? "" });
    setEditing(a);
  }

  async function save() {
    setBusy(true);
    setErrors({});
    const payload = { ...draft, publisher: emptyToNull(draft.publisher), affectedFromVersion: emptyToNull(draft.affectedFromVersion), reference: emptyToNull(draft.reference) };
    try {
      if (editing === "new") await api.post("security/advisories", payload);
      else if (editing) await api.put(`security/advisories/${editing.id}`, payload);
      toast({ tone: "success", title: "Advisory saved", description: "Devices were re-evaluated." });
      setEditing(null);
      await resource.reload();
    } catch (e) {
      setErrors(fieldErrorsFrom(e));
      toast({ tone: "error", title: "Could not save", description: messageFrom(e) });
    } finally {
      setBusy(false);
    }
  }

  async function act(fn: () => Promise<unknown>, title: string) {
    try {
      await fn();
      await resource.reload();
    } catch (e) {
      toast({ tone: "error", title, description: messageFrom(e) });
    }
  }

  const set = (k: keyof typeof empty) => (e: React.ChangeEvent<HTMLInputElement | HTMLSelectElement>) => setDraft({ ...draft, [k]: e.target.value });

  return (
    <div className="space-y-4">
      <Alert tone="info" title="How vulnerable software is identified">
        Solvix compares each device&apos;s installed software (name and version) with the advisories listed here. It does not include or synchronise a vulnerability database, so a product with no advisory is
        not reported. Matching is plain text on the product name — not CPE or CVE lookup.
      </Alert>
      {isAdmin ? (
        <div className="flex justify-end">
          <Button onClick={() => open("new")}>
            <Plus className="h-4 w-4" /> New advisory
          </Button>
        </div>
      ) : null}
      <Card>
        <LoadState resource={resource}>
          {(items) => (
            <Table>
              <THead>
                <TR>
                  <TH>Advisory</TH>
                  <TH>Product contains</TH>
                  <TH>Vulnerable versions</TH>
                  <TH>Severity</TH>
                  <TH>State</TH>
                  {isAdmin ? <TH className="text-right">Actions</TH> : null}
                </TR>
              </THead>
              <TBody>
                {items.length === 0 ? (
                  <EmptyRow colSpan={6}>No advisories yet.</EmptyRow>
                ) : (
                  items.map((a) => (
                    <TR key={a.id}>
                      <TD className="font-medium">
                        {a.title}
                        {a.reference ? <p className="text-xs font-normal text-muted-foreground">{a.reference}</p> : null}
                      </TD>
                      <TD className="font-mono text-xs">{a.productPattern}</TD>
                      <TD className="text-xs">
                        {a.affectedFromVersion ? `${a.affectedFromVersion} up to ` : "below "}
                        {a.fixedVersion} (fixed)
                      </TD>
                      <TD>
                        <SeverityBadge severity={a.severity} />
                      </TD>
                      <TD>
                        <Badge tone={a.isEnabled ? "success" : "neutral"}>{a.isEnabled ? "Enabled" : "Disabled"}</Badge>
                      </TD>
                      {isAdmin ? (
                        <TD>
                          <div className="flex justify-end gap-1">
                            <Button size="sm" variant="outline" onClick={() => open(a)}>
                              Edit
                            </Button>
                            <Button size="sm" variant="ghost" onClick={() => act(() => api.post(`security/advisories/${a.id}/${a.isEnabled ? "disable" : "enable"}`), "Could not change state")}>
                              {a.isEnabled ? "Disable" : "Enable"}
                            </Button>
                            <Button size="sm" variant="ghost" className="text-destructive" onClick={() => act(() => api.delete(`security/advisories/${a.id}`), "Could not delete")}>
                              Delete
                            </Button>
                          </div>
                        </TD>
                      ) : null}
                    </TR>
                  ))
                )}
              </TBody>
            </Table>
          )}
        </LoadState>
      </Card>

      <Dialog
        open={editing !== null}
        onClose={() => setEditing(null)}
        title={editing === "new" ? "New advisory" : "Edit advisory"}
        description="Saving re-evaluates every device against the updated list."
        footer={
          <>
            <Button variant="outline" onClick={() => setEditing(null)}>
              Cancel
            </Button>
            <Button loading={busy} onClick={save}>
              Save
            </Button>
          </>
        }
      >
        <div className="space-y-3">
          <Field label="Title" htmlFor="adv-title" error={errors.title} required>
            <Input id="adv-title" value={draft.title} onChange={set("title")} maxLength={200} />
          </Field>
          <Field label="Product name contains" htmlFor="adv-product" error={errors.productPattern} hint="At least 3 characters; plain text, not a regular expression." required>
            <Input id="adv-product" value={draft.productPattern} onChange={set("productPattern")} maxLength={200} />
          </Field>
          <Field label="Publisher contains (optional)" htmlFor="adv-pub" error={errors.publisher}>
            <Input id="adv-pub" value={draft.publisher} onChange={set("publisher")} maxLength={200} />
          </Field>
          <div className="grid grid-cols-2 gap-3">
            <Field label="Vulnerable from (optional)" htmlFor="adv-from" error={errors.affectedFromVersion}>
              <Input id="adv-from" value={draft.affectedFromVersion} onChange={set("affectedFromVersion")} placeholder="10.0" />
            </Field>
            <Field label="Fixed in" htmlFor="adv-fixed" error={errors.fixedVersion} required>
              <Input id="adv-fixed" value={draft.fixedVersion} onChange={set("fixedVersion")} placeholder="12.0.4" />
            </Field>
          </div>
          <Field label="Severity" htmlFor="adv-sev" error={errors.severity}>
            <Select id="adv-sev" value={draft.severity} onChange={set("severity")}>
              {(["Critical", "High", "Medium", "Low"] as Severity[]).map((s) => (
                <option key={s}>{s}</option>
              ))}
            </Select>
          </Field>
          <Field label="Reference (CVE id or advisory URL)" htmlFor="adv-ref" error={errors.reference}>
            <Input id="adv-ref" value={draft.reference} onChange={set("reference")} maxLength={500} />
          </Field>
        </div>
      </Dialog>
    </div>
  );
}

"use client";

import * as React from "react";
import Link from "next/link";
import { Plus } from "lucide-react";
import { api } from "@/lib/api/client";
import type { PagedResult, VendorDto } from "@/lib/api/types";
import { emptyToNull, fieldErrorsFrom, messageFrom } from "@/lib/forms";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card } from "@/components/ui/card";
import { Dialog } from "@/components/ui/dialog";
import { Field } from "@/components/ui/field";
import { Checkbox, Input, Textarea } from "@/components/ui/input";
import { PageHeader } from "@/components/ui/page-header";
import { Table, THead, TBody, TR, TH, TD, EmptyRow } from "@/components/ui/table";
import { useToast } from "@/components/providers/toast-provider";

type Form = { name: string; contactName: string; email: string; phone: string; website: string; accountNumber: string; notes: string; isActive: boolean };

const empty: Form = { name: "", contactName: "", email: "", phone: "", website: "", accountNumber: "", notes: "", isActive: true };

export function VendorsView({ initial }: { initial: PagedResult<VendorDto> }) {
  const { toast } = useToast();
  const [data, setData] = React.useState(initial);
  const [editing, setEditing] = React.useState<VendorDto | "new" | null>(null);
  const [deleting, setDeleting] = React.useState<VendorDto | null>(null);
  const [search, setSearch] = React.useState("");

  const load = React.useCallback(async (term = "") => {
    setData(await api.get<PagedResult<VendorDto>>("asset-vendors", { search: term, includeInactive: true, pageSize: 200 }));
  }, []);

  return (
    <>
      <PageHeader
        title="Vendors"
        description="Suppliers, resellers, repairers and warranty providers."
        actions={
          <Button onClick={() => setEditing("new")}>
            <Plus className="h-4 w-4" /> Add vendor
          </Button>
        }
      />
      <form
        className="mb-3 flex max-w-md gap-2"
        onSubmit={(e) => {
          e.preventDefault();
          void load(search);
        }}
      >
        <Input aria-label="Search vendors" placeholder="Search name, contact or e-mail" value={search} onChange={(e) => setSearch(e.target.value)} />
        <Button type="submit" variant="outline">
          Search
        </Button>
      </form>
      <Card>
        <Table>
          <THead>
            <TR>
              <TH>Vendor</TH>
              <TH>Contact</TH>
              <TH>Account</TH>
              <TH className="text-right">Assets</TH>
              <TH />
            </TR>
          </THead>
          <TBody>
            {data.items.length === 0 ? (
              <EmptyRow colSpan={5}>No vendors yet.</EmptyRow>
            ) : (
              data.items.map((v) => (
                <TR key={v.id}>
                  <TD className="font-medium">
                    {v.name} {v.isActive ? null : <Badge>inactive</Badge>}
                    {v.website ? (
                      <a href={v.website} target="_blank" rel="noopener noreferrer" className="block text-xs font-normal text-muted-foreground hover:underline">
                        {v.website}
                      </a>
                    ) : null}
                  </TD>
                  <TD className="text-muted-foreground">{[v.contactName, v.email, v.phone].filter(Boolean).join(" · ") || "—"}</TD>
                  <TD className="text-muted-foreground">{v.accountNumber ?? "—"}</TD>
                  <TD className="text-right">
                    {v.assetCount ? (
                      <Link href={`/assets?vendorId=${v.id}`} className="hover:underline">
                        {v.assetCount}
                      </Link>
                    ) : (
                      0
                    )}
                  </TD>
                  <TD className="whitespace-nowrap text-right">
                    <Button size="sm" variant="ghost" onClick={() => setEditing(v)}>
                      Edit
                    </Button>
                    <Button size="sm" variant="ghost" onClick={() => setDeleting(v)}>
                      Delete
                    </Button>
                  </TD>
                </TR>
              ))
            )}
          </TBody>
        </Table>
      </Card>
      {editing ? <VendorDialog vendor={editing === "new" ? null : editing} onClose={() => setEditing(null)} onSaved={() => (setEditing(null), load(search))} /> : null}
      <Dialog
        open={!!deleting}
        onClose={() => setDeleting(null)}
        title={`Delete ${deleting?.name ?? "vendor"}?`}
        description="Vendors referenced by assets or maintenance records cannot be deleted; deactivate them instead."
        footer={
          <>
            <Button variant="outline" onClick={() => setDeleting(null)}>
              Cancel
            </Button>
            <Button
              variant="destructive"
              onClick={async () => {
                try {
                  await api.delete(`asset-vendors/${deleting!.id}`);
                  toast({ tone: "success", title: "Vendor deleted" });
                  setDeleting(null);
                  await load(search);
                } catch (e) {
                  toast({ tone: "error", title: "Could not delete", description: messageFrom(e) });
                }
              }}
            >
              Delete
            </Button>
          </>
        }
      >
        {null}
      </Dialog>
    </>
  );
}

function VendorDialog({ vendor, onClose, onSaved }: { vendor: VendorDto | null; onClose: () => void; onSaved: () => void }) {
  const { toast } = useToast();
  const [form, setForm] = React.useState<Form>(() =>
    vendor
      ? {
          name: vendor.name,
          contactName: vendor.contactName ?? "",
          email: vendor.email ?? "",
          phone: vendor.phone ?? "",
          website: vendor.website ?? "",
          accountNumber: vendor.accountNumber ?? "",
          notes: vendor.notes ?? "",
          isActive: vendor.isActive,
        }
      : empty,
  );
  const [errors, setErrors] = React.useState<Record<string, string>>({});
  const [busy, setBusy] = React.useState(false);
  const field = (key: Exclude<keyof Form, "isActive" | "notes">, label: string, props: React.InputHTMLAttributes<HTMLInputElement> = {}) => (
    <Field label={label} htmlFor={`vendor-${key}`} error={errors[key]} required={props.required}>
      <Input id={`vendor-${key}`} value={form[key]} onChange={(e) => setForm((f) => ({ ...f, [key]: e.target.value }))} {...props} />
    </Field>
  );
  async function save() {
    setBusy(true);
    setErrors({});
    const body = {
      name: form.name.trim(),
      contactName: emptyToNull(form.contactName),
      email: emptyToNull(form.email),
      phone: emptyToNull(form.phone),
      website: emptyToNull(form.website),
      accountNumber: emptyToNull(form.accountNumber),
      notes: emptyToNull(form.notes),
      isActive: form.isActive,
    };
    try {
      if (vendor) await api.put(`asset-vendors/${vendor.id}`, body);
      else await api.post("asset-vendors", body);
      toast({ tone: "success", title: vendor ? "Vendor updated" : "Vendor added" });
      onSaved();
    } catch (e) {
      setErrors(fieldErrorsFrom(e));
      toast({ tone: "error", title: "Could not save", description: messageFrom(e) });
    } finally {
      setBusy(false);
    }
  }
  return (
    <Dialog
      open
      onClose={onClose}
      title={vendor ? `Edit ${vendor.name}` : "Add vendor"}
      footer={
        <>
          <Button variant="outline" onClick={onClose}>
            Cancel
          </Button>
          <Button loading={busy} disabled={!form.name.trim()} onClick={save}>
            Save
          </Button>
        </>
      }
    >
      <div className="grid gap-3 sm:grid-cols-2">
        {field("name", "Name", { required: true, maxLength: 200 })}
        {field("contactName", "Contact", { maxLength: 200 })}
        {field("email", "E-mail", { type: "email", maxLength: 200 })}
        {field("phone", "Phone", { maxLength: 50 })}
        {field("website", "Website", { type: "url", maxLength: 200, placeholder: "https://" })}
        {field("accountNumber", "Account number", { maxLength: 100 })}
        <Field label="Notes" htmlFor="vendor-notes" className="sm:col-span-2">
          <Textarea id="vendor-notes" value={form.notes} onChange={(e) => setForm((f) => ({ ...f, notes: e.target.value }))} maxLength={2000} />
        </Field>
        <label className="flex items-center gap-2 text-sm">
          <Checkbox checked={form.isActive} onChange={(e) => setForm((f) => ({ ...f, isActive: e.target.checked }))} /> Active
        </label>
      </div>
    </Dialog>
  );
}

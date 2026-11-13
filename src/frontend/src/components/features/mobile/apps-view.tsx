"use client";

import * as React from "react";
import { Pencil, Plus, Trash2 } from "lucide-react";
import { api } from "@/lib/api/client";
import type { AppDto, MobilePlatform } from "@/lib/api/mdm-types";
import type { PagedResult } from "@/lib/api/types";
import { fieldErrorsFrom, messageFrom } from "@/lib/forms";
import { intOrNull, platformLabel } from "@/lib/mdm";
import { Alert } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import { Card } from "@/components/ui/card";
import { Dialog } from "@/components/ui/dialog";
import { Field } from "@/components/ui/field";
import { Input, Select } from "@/components/ui/input";
import { Table, THead, TBody, TR, TH, TD, EmptyRow } from "@/components/ui/table";
import { useToast } from "@/components/providers/toast-provider";
import { useCan } from "@/components/providers/session-provider";
import { PlatformIcon } from "./badges";

export function AppsView({ initial }: { initial: PagedResult<AppDto> }) {
  const { toast } = useToast();
  const { isAdmin } = useCan();
  const [data, setData] = React.useState(initial);
  const [search, setSearch] = React.useState("");
  const [editing, setEditing] = React.useState<AppDto | "new" | null>(null);
  const [confirmDelete, setConfirmDelete] = React.useState<AppDto | null>(null);
  const [busy, setBusy] = React.useState(false);

  const reload = React.useCallback(async (q: string) => {
    setData(await api.get<PagedResult<AppDto>>("mobile-apps", { search: q, pageSize: 50 }));
  }, []);

  async function remove(app: AppDto) {
    setBusy(true);
    try {
      await api.delete(`mobile-apps/${app.id}`);
      toast({ tone: "success", title: "App removed from catalogue" });
      setConfirmDelete(null);
      await reload(search);
    } catch (e) {
      toast({ tone: "error", title: "Could not delete", description: messageFrom(e) });
      setConfirmDelete(null);
    } finally {
      setBusy(false);
    }
  }

  return (
    <>
      <div className="mb-4 flex items-center justify-between gap-3">
        <form
          className="flex flex-1 items-center gap-2"
          onSubmit={(e) => {
            e.preventDefault();
            void reload(search);
          }}
        >
          <Input placeholder="Search apps…" value={search} onChange={(e) => setSearch(e.target.value)} className="max-w-xs" aria-label="Search apps" />
          <Button type="submit" variant="outline">Search</Button>
        </form>
        {isAdmin ? <Button onClick={() => setEditing("new")}><Plus className="h-4 w-4" /> Add app</Button> : null}
      </div>
      <Card>
        <Table>
          <THead><TR><TH>App</TH><TH>Platform</TH><TH>Identifier</TH><TH>Store id</TH>{isAdmin ? <TH className="text-right">Actions</TH> : null}</TR></THead>
          <TBody>
            {data.items.length === 0 ? (
              <EmptyRow colSpan={isAdmin ? 5 : 4}>The catalogue is empty.</EmptyRow>
            ) : (
              data.items.map((a) => (
                <TR key={a.id}>
                  <TD className="font-medium">{a.name}</TD>
                  <TD><span className="flex items-center gap-2"><PlatformIcon platform={a.platform} />{platformLabel[a.platform]}</span></TD>
                  <TD className="font-mono text-xs">{a.identifier}</TD>
                  <TD className="text-muted-foreground">{a.storeId ?? "—"}</TD>
                  {isAdmin ? (
                    <TD>
                      <div className="flex justify-end gap-1">
                        <Button variant="ghost" size="icon" aria-label={`Edit ${a.name}`} onClick={() => setEditing(a)}><Pencil className="h-4 w-4" /></Button>
                        <Button variant="ghost" size="icon" aria-label={`Delete ${a.name}`} className="text-destructive" onClick={() => setConfirmDelete(a)}><Trash2 className="h-4 w-4" /></Button>
                      </div>
                    </TD>
                  ) : null}
                </TR>
              ))
            )}
          </TBody>
        </Table>
      </Card>
      {editing ? (
        <AppDialog
          app={editing === "new" ? null : editing}
          onClose={() => setEditing(null)}
          onSaved={async () => {
            setEditing(null);
            toast({ tone: "success", title: "App saved" });
            await reload(search);
          }}
        />
      ) : null}
      <Dialog
        open={confirmDelete !== null}
        onClose={() => setConfirmDelete(null)}
        title="Remove app?"
        description={confirmDelete ? `${confirmDelete.name} will be removed from the catalogue. Apps assigned to a policy cannot be removed.` : undefined}
        footer={
          <>
            <Button variant="outline" onClick={() => setConfirmDelete(null)}>Cancel</Button>
            <Button variant="destructive" loading={busy} onClick={() => confirmDelete && remove(confirmDelete)}>Remove</Button>
          </>
        }
      >
        <p className="text-sm text-muted-foreground">Devices that already have the app keep it.</p>
      </Dialog>
    </>
  );
}

function AppDialog({ app, onClose, onSaved }: { app: AppDto | null; onClose: () => void; onSaved: () => Promise<void> }) {
  const [platform, setPlatform] = React.useState<MobilePlatform>(app?.platform ?? "Android");
  const [name, setName] = React.useState(app?.name ?? "");
  const [identifier, setIdentifier] = React.useState(app?.identifier ?? "");
  const [storeId, setStoreId] = React.useState(app?.storeId?.toString() ?? "");
  const [errors, setErrors] = React.useState<Record<string, string>>({});
  const [error, setError] = React.useState<string | null>(null);
  const [pending, setPending] = React.useState(false);

  async function submit(event: React.FormEvent) {
    event.preventDefault();
    setPending(true);
    setError(null);
    setErrors({});
    try {
      const store = platform === "Ios" ? intOrNull(storeId) : null;
      if (app) await api.put(`mobile-apps/${app.id}`, { name, storeId: store });
      else await api.post("mobile-apps", { platform, name, identifier, storeId: store });
      await onSaved();
    } catch (e) {
      setErrors(fieldErrorsFrom(e));
      setError(messageFrom(e));
    } finally {
      setPending(false);
    }
  }

  return (
    <Dialog
      open
      onClose={onClose}
      title={app ? `Edit ${app.name}` : "Add app"}
      footer={
        <>
          <Button variant="outline" onClick={onClose}>Cancel</Button>
          <Button type="submit" form="app-form" loading={pending}>{app ? "Save" : "Add"}</Button>
        </>
      }
    >
      <form id="app-form" onSubmit={submit} className="space-y-4">
        {error ? <Alert tone="error">{error}</Alert> : null}
        <Field label="Platform" htmlFor="app-platform">
          <Select id="app-platform" value={platform} disabled={app !== null} onChange={(e) => setPlatform(e.target.value as MobilePlatform)}>
            <option value="Android">Android (Managed Google Play)</option>
            <option value="Ios">iOS / iPadOS (App Store)</option>
          </Select>
        </Field>
        <Field label="Name" htmlFor="app-name" error={errors.name} required>
          <Input id="app-name" value={name} onChange={(e) => setName(e.target.value)} required maxLength={200} autoFocus />
        </Field>
        <Field label={platform === "Android" ? "Package name" : "Bundle identifier"} htmlFor="app-identifier" error={errors.identifier} required>
          <Input id="app-identifier" value={identifier} disabled={app !== null} onChange={(e) => setIdentifier(e.target.value)} required />
        </Field>
        {platform === "Ios" ? (
          <Field label="App Store id" htmlFor="app-store-id" error={errors.storeId} hint="Needed to install the app silently">
            <Input id="app-store-id" inputMode="numeric" value={storeId} onChange={(e) => setStoreId(e.target.value)} />
          </Field>
        ) : null}
      </form>
    </Dialog>
  );
}

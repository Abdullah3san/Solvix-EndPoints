"use client";

import * as React from "react";
import Link from "next/link";
import { useRouter } from "next/navigation";
import { api } from "@/lib/api/client";
import type { TenantDto } from "@/lib/api/types";
import { messageFrom } from "@/lib/forms";
import { Button } from "@/components/ui/button";
import { Badge } from "@/components/ui/badge";
import { Field } from "@/components/ui/field";
import { Input } from "@/components/ui/input";
import { Alert } from "@/components/ui/alert";
import { Card, CardContent, CardDescription, CardFooter, CardHeader, CardTitle } from "@/components/ui/card";
import { useToast } from "@/components/providers/toast-provider";
import { useNotifications } from "@/components/providers/notifications-provider";
import { useCan } from "@/components/providers/session-provider";
import { formatDate } from "@/lib/utils";

export function WorkspaceSettings({ tenant: initialTenant }: { tenant: TenantDto }) {
  const router = useRouter();
  const { toast } = useToast();
  const { notify } = useNotifications();
  const canManage = useCan("tenant.manage");
  const canSeeUsers = useCan("users.view");
  const [tenant, setTenant] = React.useState(initialTenant);
  const [name, setName] = React.useState(initialTenant.name);
  const [error, setError] = React.useState<string | null>(null);
  const [saving, setSaving] = React.useState(false);

  async function save(event: React.FormEvent) {
    event.preventDefault();
    setSaving(true);
    setError(null);
    try {
      const updated = await api.put<TenantDto>("tenant", { name });
      setTenant(updated);
      toast({ tone: "success", title: "Workspace renamed" });
      notify({ tone: "info", title: "Workspace renamed", description: updated.name });
      router.refresh();
    } catch (e) {
      setError(messageFrom(e));
    } finally {
      setSaving(false);
    }
  }

  return (
    <div className="space-y-6">
      <form onSubmit={save}>
        <Card>
          <CardHeader>
            <CardTitle>Workspace</CardTitle>
            <CardDescription>The workspace id is permanent.</CardDescription>
          </CardHeader>
          <CardContent className="grid gap-4 sm:grid-cols-2">
            {error ? (
              <div className="sm:col-span-2">
                <Alert tone="error">{error}</Alert>
              </div>
            ) : null}
            <Field label="Name" htmlFor="tenant-name" required>
              <Input id="tenant-name" value={name} onChange={(e) => setName(e.target.value)} disabled={!canManage} required />
            </Field>
            <Field label="Workspace id" htmlFor="tenant-slug" hint="Used in sign-in and URLs">
              <Input id="tenant-slug" value={tenant.slug} readOnly disabled className="font-mono" />
            </Field>
            <div className="flex items-center justify-between gap-4 text-sm sm:col-span-2">
              <span className="text-muted-foreground">Status</span>
              <Badge tone={tenant.status === "Active" ? "success" : "warning"}>{tenant.status}</Badge>
            </div>
          </CardContent>
          <CardFooter className="justify-between">
            <p className="text-xs text-muted-foreground">Created {formatDate(tenant.createdAtUtc)}</p>
            {canManage ? (
              <Button type="submit" loading={saving} disabled={name.trim() === tenant.name}>
                Save
              </Button>
            ) : (
              <p className="text-xs text-muted-foreground">Renaming needs the workspace settings permission.</p>
            )}
          </CardFooter>
        </Card>
      </form>
      {canSeeUsers ? (
        <p className="text-sm text-muted-foreground">
          Looking for people?{" "}
          <Link href="/settings/users" className="font-medium text-primary hover:underline">
            Users and invitations
          </Link>{" "}
          have moved to the Access section.
        </p>
      ) : null}
    </div>
  );
}

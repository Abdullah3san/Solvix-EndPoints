"use client";

import * as React from "react";
import { api } from "@/lib/api/client";
import type { AssetSettingsDto } from "@/lib/api/types";
import { fieldErrorsFrom, messageFrom } from "@/lib/forms";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardFooter, CardHeader, CardTitle } from "@/components/ui/card";
import { Field } from "@/components/ui/field";
import { Checkbox, Input } from "@/components/ui/input";
import { useToast } from "@/components/providers/toast-provider";

export function AssetSettingsForm({ initial }: { initial: AssetSettingsDto }) {
  const { toast } = useToast();
  const [form, setForm] = React.useState(initial);
  const [errors, setErrors] = React.useState<Record<string, string>>({});
  const [busy, setBusy] = React.useState(false);

  async function save(e: React.FormEvent) {
    e.preventDefault();
    setBusy(true);
    setErrors({});
    try {
      setForm(await api.put<AssetSettingsDto>("asset-settings", form));
      toast({ tone: "success", title: "Settings saved" });
    } catch (err) {
      setErrors(fieldErrorsFrom(err));
      toast({ tone: "error", title: "Could not save", description: messageFrom(err) });
    } finally {
      setBusy(false);
    }
  }

  return (
    <form onSubmit={save} className="max-w-2xl space-y-4">
      <Card>
        <CardHeader>
          <CardTitle>Numbering and defaults</CardTitle>
          <CardDescription>
            New assets are numbered {form.tagPrefix || "AST"}-&lt;category&gt;-000001. Changing the prefix starts a new sequence; existing tags never change.
          </CardDescription>
        </CardHeader>
        <CardContent className="grid gap-3 sm:grid-cols-3">
          <Field label="Tag prefix" htmlFor="set-prefix" error={errors.tagPrefix} required>
            <Input id="set-prefix" value={form.tagPrefix} maxLength={10} onChange={(e) => setForm({ ...form, tagPrefix: e.target.value.toUpperCase() })} />
          </Field>
          <Field label="Default currency" htmlFor="set-currency" error={errors.defaultCurrency} required>
            <Input id="set-currency" value={form.defaultCurrency} maxLength={3} onChange={(e) => setForm({ ...form, defaultCurrency: e.target.value.toUpperCase() })} />
          </Field>
          <Field label="Warranty alert (days)" htmlFor="set-alert" error={errors.warrantyAlertDays} required>
            <Input id="set-alert" type="number" min={1} max={365} value={form.warrantyAlertDays} onChange={(e) => setForm({ ...form, warrantyAlertDays: Number(e.target.value) })} />
          </Field>
        </CardContent>
      </Card>
      <Card>
        <CardHeader>
          <CardTitle>Endpoint matching</CardTitle>
          <CardDescription>
            When a Windows device enrolls, its serial number, manufacturer and model are compared with the asset register. By default every proposed association waits for an administrator.
          </CardDescription>
        </CardHeader>
        <CardContent>
          <label className="flex items-start gap-2 text-sm">
            <Checkbox className="mt-0.5" checked={form.autoApproveHighConfidenceMatches} onChange={(e) => setForm({ ...form, autoApproveHighConfidenceMatches: e.target.checked })} />
            <span>
              Approve <strong>high-confidence, unambiguous</strong> matches automatically (exactly one asset and one endpoint share the serial number, the manufacturer matches, and neither is linked yet). Ambiguous or
              weaker matches always wait for review.
            </span>
          </label>
        </CardContent>
        <CardFooter className="justify-end">
          <Button type="submit" loading={busy}>
            Save settings
          </Button>
        </CardFooter>
      </Card>
    </form>
  );
}

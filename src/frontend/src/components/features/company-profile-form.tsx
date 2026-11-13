"use client";

import * as React from "react";
import { useRouter } from "next/navigation";
import { api } from "@/lib/api/client";
import type { CompanyProfileDto } from "@/lib/api/types";
import { fieldErrorsFrom, messageFrom, emptyToNull } from "@/lib/forms";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardFooter, CardHeader, CardTitle } from "@/components/ui/card";
import { Field } from "@/components/ui/field";
import { Input } from "@/components/ui/input";
import { Alert } from "@/components/ui/alert";
import { AddressFields } from "./address-fields";
import { useToast } from "@/components/providers/toast-provider";
import { useNotifications } from "@/components/providers/notifications-provider";
import { useCan } from "@/components/providers/session-provider";
import { formatDate } from "@/lib/utils";

export function CompanyProfileForm({ profile: initial }: { profile: CompanyProfileDto }) {
  const router = useRouter();
  const { toast } = useToast();
  const { notify } = useNotifications();
  const canManage = useCan("organization.manage");
  const [profile, setProfile] = React.useState(initial);
  const [errors, setErrors] = React.useState<Record<string, string>>({});
  const [error, setError] = React.useState<string | null>(null);
  const [pending, setPending] = React.useState(false);

  const set = (key: keyof CompanyProfileDto) => (event: React.ChangeEvent<HTMLInputElement>) =>
    setProfile((p) => ({ ...p, [key]: event.target.value }));

  async function onSubmit(event: React.FormEvent) {
    event.preventDefault();
    setPending(true);
    setError(null);
    try {
      const updated = await api.put<CompanyProfileDto>("company-profile", {
        legalName: profile.legalName,
        tradeName: emptyToNull(profile.tradeName),
        taxId: emptyToNull(profile.taxId),
        registrationNumber: emptyToNull(profile.registrationNumber),
        industry: emptyToNull(profile.industry),
        website: emptyToNull(profile.website),
        contactEmail: emptyToNull(profile.contactEmail),
        contactPhone: emptyToNull(profile.contactPhone),
        timeZone: emptyToNull(profile.timeZone),
        address: profile.address,
      });
      setProfile(updated);
      setErrors({});
      toast({ tone: "success", title: "Company profile saved" });
      notify({ tone: "success", title: "Company profile updated", description: updated.legalName });
      router.refresh();
    } catch (e) {
      setErrors(fieldErrorsFrom(e));
      setError(messageFrom(e));
    } finally {
      setPending(false);
    }
  }

  return (
    <form onSubmit={onSubmit}>
      <Card>
        <CardHeader>
          <CardTitle>Company details</CardTitle>
          <CardDescription>Legal identity and contact information for this workspace.</CardDescription>
        </CardHeader>
        <CardContent className="space-y-6">
          {error ? <Alert tone="error">{error}</Alert> : null}
          <fieldset disabled={!canManage || pending} className="grid gap-4 sm:grid-cols-2">
            <Field label="Legal name" htmlFor="legalName" error={errors.legalName} required>
              <Input id="legalName" value={profile.legalName} onChange={set("legalName")} required aria-invalid={!!errors.legalName} />
            </Field>
            <Field label="Trade name" htmlFor="tradeName" error={errors.tradeName}>
              <Input id="tradeName" value={profile.tradeName ?? ""} onChange={set("tradeName")} />
            </Field>
            <Field label="Tax id" htmlFor="taxId" error={errors.taxId}>
              <Input id="taxId" value={profile.taxId ?? ""} onChange={set("taxId")} />
            </Field>
            <Field label="Registration number" htmlFor="registrationNumber" error={errors.registrationNumber}>
              <Input id="registrationNumber" value={profile.registrationNumber ?? ""} onChange={set("registrationNumber")} />
            </Field>
            <Field label="Industry" htmlFor="industry" error={errors.industry}>
              <Input id="industry" value={profile.industry ?? ""} onChange={set("industry")} />
            </Field>
            <Field label="Website" htmlFor="website" error={errors.website} hint="Absolute http(s) URL">
              <Input id="website" type="url" value={profile.website ?? ""} onChange={set("website")} aria-invalid={!!errors.website} />
            </Field>
            <Field label="Contact e-mail" htmlFor="contactEmail" error={errors.contactEmail}>
              <Input id="contactEmail" type="email" value={profile.contactEmail ?? ""} onChange={set("contactEmail")} aria-invalid={!!errors.contactEmail} />
            </Field>
            <Field label="Contact phone" htmlFor="contactPhone" error={errors.contactPhone}>
              <Input id="contactPhone" value={profile.contactPhone ?? ""} onChange={set("contactPhone")} />
            </Field>
            <Field label="Time zone" htmlFor="timeZone" error={errors.timeZone} hint="IANA id, e.g. Europe/Berlin">
              <Input id="timeZone" value={profile.timeZone} onChange={set("timeZone")} aria-invalid={!!errors.timeZone} />
            </Field>
          </fieldset>
          <fieldset disabled={!canManage || pending}>
            <AddressFields value={profile.address} onChange={(address) => setProfile((p) => ({ ...p, address }))} errors={errors} />
          </fieldset>
        </CardContent>
        <CardFooter className="justify-between">
          <p className="text-xs text-muted-foreground">Last updated {formatDate(profile.updatedAtUtc)}</p>
          {canManage ? (
            <Button type="submit" loading={pending}>
              Save changes
            </Button>
          ) : (
            <p className="text-xs text-muted-foreground">Only admins can edit the company profile.</p>
          )}
        </CardFooter>
      </Card>
    </form>
  );
}

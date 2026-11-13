"use client";

import * as React from "react";
import { api } from "@/lib/api/client";
import type { SecurityPolicyDto, SecurityPolicySettings } from "@/lib/api/types";
import { fieldErrorsFrom, messageFrom } from "@/lib/forms";
import { formatDate } from "@/lib/utils";
import { Button } from "@/components/ui/button";
import { Field } from "@/components/ui/field";
import { Checkbox, Input } from "@/components/ui/input";
import { Alert } from "@/components/ui/alert";
import { Card, CardContent, CardDescription, CardFooter, CardHeader, CardTitle } from "@/components/ui/card";
import { useToast } from "@/components/providers/toast-provider";
import { useCan } from "@/components/providers/session-provider";

type NumberKey = { [K in keyof SecurityPolicySettings]: SecurityPolicySettings[K] extends number ? K : never }[keyof SecurityPolicySettings];
type BoolKey = { [K in keyof SecurityPolicySettings]: SecurityPolicySettings[K] extends boolean ? K : never }[keyof SecurityPolicySettings];

export function SecurityPolicyForm({ initial }: { initial: SecurityPolicyDto }) {
  const { toast } = useToast();
  const canManage = useCan("security.manage");
  const [saved, setSaved] = React.useState(initial);
  const [draft, setDraft] = React.useState(initial.policy);
  const [errors, setErrors] = React.useState<Record<string, string>>({});
  const [error, setError] = React.useState<string | null>(null);
  const [pending, setPending] = React.useState(false);
  const baseline = saved.baseline;
  const dirty = JSON.stringify(draft) !== JSON.stringify(saved.policy);

  async function save(event: React.FormEvent) {
    event.preventDefault();
    setPending(true);
    setError(null);
    setErrors({});
    try {
      const result = await api.put<SecurityPolicyDto>("security/policy", draft);
      setSaved(result);
      setDraft(result.policy);
      toast({ tone: "success", title: "Security policy saved" });
    } catch (e) {
      setErrors(fieldErrorsFrom(e));
      setError(messageFrom(e));
    } finally {
      setPending(false);
    }
  }

  const number = (key: NumberKey, label: string, hint: string) => (
    <Field label={label} htmlFor={key} error={errors[key]} hint={hint}>
      <Input
        id={key}
        type="number"
        inputMode="numeric"
        value={draft[key]}
        onChange={(e) => setDraft({ ...draft, [key]: Number(e.target.value) })}
        disabled={!canManage}
      />
    </Field>
  );

  const toggle = (key: BoolKey, label: string, locked = false) => (
    <label className="flex items-center gap-3 text-sm">
      <Checkbox checked={draft[key]} onChange={(e) => setDraft({ ...draft, [key]: e.target.checked })} disabled={!canManage || locked} />
      <span>
        {label}
        {locked ? <span className="ml-1 text-xs text-muted-foreground">(platform baseline)</span> : null}
      </span>
      {errors[key] ? <span className="text-xs text-destructive">{errors[key]}</span> : null}
    </label>
  );

  return (
    <form onSubmit={save} className="space-y-6">
      {error ? <Alert tone="error">{error}</Alert> : null}
      {!canManage ? <Alert tone="info">You can view this policy. Changing it needs the security.manage permission.</Alert> : null}
      <Card>
        <CardHeader>
          <CardTitle>Multi-factor authentication</CardTitle>
          <CardDescription>When required, members without a second factor can only reach their own account pages until they set one up.</CardDescription>
        </CardHeader>
        <CardContent>{toggle("requireMfa", "Require multi-factor authentication for every member")}</CardContent>
      </Card>
      <Card>
        <CardHeader>
          <CardTitle>Passwords</CardTitle>
          <CardDescription>
            Applies to members of this workspace. Someone who belongs to several workspaces must meet the strictest of their policies. The platform
            baseline (minimum {baseline.passwordMinLength} characters) cannot be lowered.
          </CardDescription>
        </CardHeader>
        <CardContent className="grid gap-4 sm:grid-cols-3">
          {number("passwordMinLength", "Minimum length", `${baseline.passwordMinLength}–128`)}
          {number("passwordHistoryCount", "Remembered passwords", "Cannot be reused. 0–24, 0 turns it off.")}
          {number("passwordMaxAgeDays", "Maximum age (days)", "0 means passwords do not expire.")}
          <div className="grid gap-2 sm:col-span-3 sm:grid-cols-2">
            {toggle("passwordRequireUppercase", "Require an upper-case letter", baseline.passwordRequireUppercase)}
            {toggle("passwordRequireLowercase", "Require a lower-case letter", baseline.passwordRequireLowercase)}
            {toggle("passwordRequireDigit", "Require a digit", baseline.passwordRequireDigit)}
            {toggle("passwordRequireSymbol", "Require a symbol", baseline.passwordRequireSymbol)}
          </div>
        </CardContent>
      </Card>
      <Card>
        <CardHeader>
          <CardTitle>Sessions</CardTitle>
          <CardDescription>Applies to sessions started after the change.</CardDescription>
        </CardHeader>
        <CardContent className="grid gap-4 sm:grid-cols-2">
          {number("sessionIdleTimeoutMinutes", "Sign out after inactivity (minutes)", "5 minutes to 30 days")}
          {number("sessionAbsoluteLifetimeHours", "Maximum session length (hours)", "1 hour to 90 days, whatever the activity")}
        </CardContent>
        <CardFooter className="justify-between">
          <p className="text-xs text-muted-foreground">Last changed {formatDate(saved.updatedAtUtc)}</p>
          {canManage ? (
            <div className="flex gap-2">
              <Button variant="outline" onClick={() => setDraft(saved.policy)} disabled={!dirty || pending}>
                Discard
              </Button>
              <Button type="submit" loading={pending} disabled={!dirty}>
                Save policy
              </Button>
            </div>
          ) : null}
        </CardFooter>
      </Card>
    </form>
  );
}

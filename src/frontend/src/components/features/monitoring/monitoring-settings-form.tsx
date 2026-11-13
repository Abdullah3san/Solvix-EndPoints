"use client";

import * as React from "react";
import { api } from "@/lib/api/client";
import type { MonitoringSettingsDto } from "@/lib/api/types";
import { fieldErrorsFrom, messageFrom } from "@/lib/forms";
import { formatDuration } from "@/lib/monitoring";
import { Alert } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardFooter, CardHeader, CardTitle } from "@/components/ui/card";
import { Field } from "@/components/ui/field";
import { Checkbox, Input } from "@/components/ui/input";
import { useCan } from "@/components/providers/session-provider";
import { useToast } from "@/components/providers/toast-provider";

/** Client-side mirror of the server rules (the API remains authoritative). */
export function validateThresholds(heartbeat: number, warning: number, offline: number): Record<string, string> {
  const errors: Record<string, string> = {};
  if (!Number.isInteger(heartbeat) || heartbeat < 15 || heartbeat > 3600) errors.heartbeatIntervalSeconds = "Between 15 seconds and 1 hour.";
  if (!Number.isInteger(warning) || warning < 2 * heartbeat) errors.warningAfterSeconds = "At least twice the heartbeat interval.";
  if (!Number.isInteger(offline) || offline <= warning) errors.offlineAfterSeconds = "Must be greater than the warning threshold.";
  else if (offline > 7 * 24 * 3600) errors.offlineAfterSeconds = "At most 7 days.";
  return errors;
}

export function MonitoringSettingsForm({ initial }: { initial: MonitoringSettingsDto }) {
  const canManage = useCan("devices.manage");
  const { toast } = useToast();
  const [values, setValues] = React.useState(initial);
  const [errors, setErrors] = React.useState<Record<string, string>>({});
  const [busy, setBusy] = React.useState(false);

  function set<K extends keyof MonitoringSettingsDto>(key: K, value: MonitoringSettingsDto[K]) {
    setValues((v) => ({ ...v, [key]: value }));
  }

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    const local = validateThresholds(values.heartbeatIntervalSeconds, values.warningAfterSeconds, values.offlineAfterSeconds);
    setErrors(local);
    if (Object.keys(local).length) return;
    setBusy(true);
    try {
      const saved = await api.put<MonitoringSettingsDto>("monitoring/settings", {
        heartbeatIntervalSeconds: values.heartbeatIntervalSeconds,
        warningAfterSeconds: values.warningAfterSeconds,
        offlineAfterSeconds: values.offlineAfterSeconds,
        offlineAlertsEnabled: values.offlineAlertsEnabled,
        recoveryNotificationsEnabled: values.recoveryNotificationsEnabled,
      });
      setValues(saved);
      toast({ tone: "success", title: "Monitoring settings saved" });
    } catch (err) {
      setErrors(fieldErrorsFrom(err));
      toast({ tone: "error", title: "Could not save settings", description: messageFrom(err) });
    } finally {
      setBusy(false);
    }
  }

  const number = (key: "heartbeatIntervalSeconds" | "warningAfterSeconds" | "offlineAfterSeconds") => ({
    id: key,
    type: "number",
    min: 15,
    value: Number.isNaN(values[key]) ? "" : values[key],
    disabled: !canManage,
    onChange: (e: React.ChangeEvent<HTMLInputElement>) => set(key, e.target.valueAsNumber),
  });

  return (
    <form onSubmit={submit} className="max-w-2xl space-y-4">
      {!canManage ? <Alert tone="info">Changing monitoring settings requires the “Manage devices” permission.</Alert> : null}
      <Card>
        <CardHeader>
          <CardTitle>Connectivity thresholds</CardTitle>
          <CardDescription>
            Based on agent contact only. A device is online below the warning threshold, in warning until the offline threshold, and
            offline only once that threshold has been exceeded.
          </CardDescription>
        </CardHeader>
        <CardContent className="grid gap-4 sm:grid-cols-3">
          <Field label="Heartbeat interval (s)" htmlFor="heartbeatIntervalSeconds" error={errors.heartbeatIntervalSeconds} hint={formatDuration(values.heartbeatIntervalSeconds)}>
            <Input {...number("heartbeatIntervalSeconds")} />
          </Field>
          <Field label="Warning after (s)" htmlFor="warningAfterSeconds" error={errors.warningAfterSeconds} hint={formatDuration(values.warningAfterSeconds)}>
            <Input {...number("warningAfterSeconds")} />
          </Field>
          <Field label="Offline after (s)" htmlFor="offlineAfterSeconds" error={errors.offlineAfterSeconds} hint={formatDuration(values.offlineAfterSeconds)}>
            <Input {...number("offlineAfterSeconds")} />
          </Field>
        </CardContent>
      </Card>
      <Card>
        <CardHeader>
          <CardTitle>Notifications</CardTitle>
        </CardHeader>
        <CardContent className="space-y-3 text-sm">
          <label className="flex items-center gap-2">
            <Checkbox checked={values.offlineAlertsEnabled} disabled={!canManage} onChange={(e) => set("offlineAlertsEnabled", e.target.checked)} />
            Raise an alert when a device goes offline
          </label>
          <label className="flex items-center gap-2">
            <Checkbox
              checked={values.recoveryNotificationsEnabled}
              disabled={!canManage}
              onChange={(e) => set("recoveryNotificationsEnabled", e.target.checked)}
            />
            Notify when an offline device recovers
          </label>
        </CardContent>
        {canManage ? (
          <CardFooter className="justify-between">
            <span className="text-xs text-muted-foreground">{values.isDefault ? "Using platform defaults." : "Custom settings for this workspace."}</span>
            <Button type="submit" loading={busy}>
              Save
            </Button>
          </CardFooter>
        ) : null}
      </Card>
    </form>
  );
}

"use client";

import * as React from "react";
import { ShieldAlert } from "lucide-react";
import { api } from "@/lib/api/client";
import type { AppDto, CapabilitiesDto, CommandDto, DeviceCommandType, DeviceDto } from "@/lib/api/mdm-types";
import type { PagedResult } from "@/lib/api/types";
import { fieldErrorsFrom, messageFrom } from "@/lib/forms";
import { commandLabel, destructiveCommands, deviceTitle } from "@/lib/mdm";
import { Alert } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import { Dialog } from "@/components/ui/dialog";
import { Field } from "@/components/ui/field";
import { Input, Select, Textarea } from "@/components/ui/input";
import { useToast } from "@/components/providers/toast-provider";

/** Commands that are meaningful to send by hand; inventory/policy refresh are handled by sync and assignment. */
const manual: DeviceCommandType[] = ["Lock", "ResetPasscode", "Reboot", "InstallApp", "RemoveApp", "Retire", "Wipe"];

export function CommandDialog({
  device,
  capabilities,
  onClose,
  onDone,
}: {
  device: DeviceDto;
  capabilities: CapabilitiesDto;
  onClose: () => void;
  onDone: () => Promise<void>;
}) {
  const { toast } = useToast();
  const offered = manual.filter((c) => capabilities.commands.includes(c));
  const [type, setType] = React.useState<DeviceCommandType>(offered[0] ?? "Lock");
  const [reason, setReason] = React.useState("");
  const [message, setMessage] = React.useState("");
  const [appId, setAppId] = React.useState("");
  const [apps, setApps] = React.useState<AppDto[]>([]);
  const [errors, setErrors] = React.useState<Record<string, string>>({});
  const [error, setError] = React.useState<string | null>(null);
  const [pending, setPending] = React.useState(false);
  const destructive = destructiveCommands.has(type);
  const needsApp = type === "InstallApp" || type === "RemoveApp";

  React.useEffect(() => {
    if (!needsApp) return;
    api
      .get<PagedResult<AppDto>>("mobile-apps", { platform: device.platform, pageSize: 200 })
      .then((r) => setApps(r.items))
      .catch((e) => setError(messageFrom(e)));
  }, [needsApp, device.platform]);

  async function submit(event: React.FormEvent) {
    event.preventDefault();
    setPending(true);
    setError(null);
    setErrors({});
    try {
      const created = await api.post<CommandDto>(`mobile-devices/${device.id}/commands`, {
        type,
        reason: reason.trim() || null,
        appId: needsApp ? appId || null : null,
        message: type === "Lock" ? message.trim() || null : null,
      });
      toast({
        tone: "success",
        title: created.state === "PendingApproval" ? "Sent for approval" : `${commandLabel[type]} requested`,
        description: created.state === "PendingApproval" ? "Another administrator must approve it before anything happens." : undefined,
      });
      await onDone();
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
      title={`Send command to ${deviceTitle(device)}`}
      footer={
        <>
          <Button variant="outline" onClick={onClose}>Cancel</Button>
          <Button type="submit" form="command-form" variant={destructive ? "destructive" : "primary"} loading={pending} disabled={offered.length === 0}>
            {destructive ? "Request approval" : "Send"}
          </Button>
        </>
      }
    >
      <form id="command-form" onSubmit={submit} className="space-y-4">
        {error ? <Alert tone="error">{error}</Alert> : null}
        {offered.length === 0 ? <Alert tone="info">This device offers no manual commands.</Alert> : null}
        <Field label="Command" htmlFor="command-type">
          <Select id="command-type" value={type} onChange={(e) => setType(e.target.value as DeviceCommandType)}>
            {offered.map((c) => (
              <option key={c} value={c}>{commandLabel[c]}</option>
            ))}
          </Select>
        </Field>

        {type === "Lock" ? (
          <Field label="Message on lock screen" htmlFor="command-message" hint="Optional, iOS only" error={errors.message}>
            <Input id="command-message" value={message} onChange={(e) => setMessage(e.target.value)} maxLength={200} />
          </Field>
        ) : null}

        {needsApp ? (
          <Field label="Application" htmlFor="command-app" error={errors.appId} required>
            <Select id="command-app" value={appId} onChange={(e) => setAppId(e.target.value)} required>
              <option value="">Select an app…</option>
              {apps.map((a) => (
                <option key={a.id} value={a.id}>{a.name} ({a.identifier})</option>
              ))}
            </Select>
          </Field>
        ) : null}

        {destructive ? (
          <>
            <Alert tone="error" title={type === "Wipe" ? "This erases the whole device" : "This removes management and work data"}>
              <span className="flex items-start gap-2">
                <ShieldAlert className="mt-0.5 h-4 w-4 shrink-0" aria-hidden />
                <span>
                  {type === "Wipe"
                    ? "All data on the device is lost. "
                    : device.ownership === "PersonallyOwned"
                      ? "Only work apps and data are removed; the user's personal data is not touched. "
                      : capabilities.canFactoryReset
                        ? "This device has no personal profile, so removing management also resets it. "
                        : "Management and work data are removed. "}
                  Nothing is sent until a different administrator approves.
                </span>
              </span>
            </Alert>
            <Field label="Reason" htmlFor="command-reason" error={errors.reason} hint="Recorded in the audit trail" required>
              <Textarea id="command-reason" value={reason} onChange={(e) => setReason(e.target.value)} required maxLength={500} />
            </Field>
          </>
        ) : null}
      </form>
    </Dialog>
  );
}

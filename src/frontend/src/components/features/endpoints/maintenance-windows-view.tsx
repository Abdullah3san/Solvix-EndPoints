"use client";

import * as React from "react";
import { Plus, Trash2 } from "lucide-react";
import { api } from "@/lib/api/client";
import type { MaintenanceWindowDto } from "@/lib/api/endpoint-types";
import { fieldErrorsFrom, messageFrom } from "@/lib/forms";
import { Button } from "@/components/ui/button";
import { Badge } from "@/components/ui/badge";
import { Card } from "@/components/ui/card";
import { Dialog } from "@/components/ui/dialog";
import { Field } from "@/components/ui/field";
import { Checkbox, Input } from "@/components/ui/input";
import { Table, THead, TBody, TR, TH, TD, EmptyRow } from "@/components/ui/table";
import { useToast } from "@/components/providers/toast-provider";
import { useCan } from "@/components/providers/session-provider";
import { NumberInput } from "./common";

const days = ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"] as const;

export function MaintenanceWindowsView({ initial }: { initial: MaintenanceWindowDto[] }) {
  const { toast } = useToast();
  const { isAdmin } = useCan();
  const [windows, setWindows] = React.useState(initial);
  const [creating, setCreating] = React.useState(false);

  const reload = React.useCallback(async () => setWindows(await api.get<MaintenanceWindowDto[]>("maintenance-windows")), []);

  return (
    <div className="space-y-4">
      {isAdmin ? (
        <div className="flex justify-end">
          <Button onClick={() => setCreating(true)}>
            <Plus className="h-4 w-4" /> New window
          </Button>
        </div>
      ) : null}
      <Card>
        <Table>
          <THead>
            <TR>
              <TH>Name</TH>
              <TH>Schedule</TH>
              <TH>Now</TH>
              <TH>Next opening</TH>
              {isAdmin ? <TH className="text-right">Actions</TH> : null}
            </TR>
          </THead>
          <TBody>
            {windows.length === 0 ? (
              <EmptyRow colSpan={5}>No maintenance windows. Without one, deployments and configuration changes may run at any time.</EmptyRow>
            ) : (
              windows.map((w) => (
                <TR key={w.id}>
                  <TD className="font-medium">{w.name}</TD>
                  <TD className="text-muted-foreground">
                    {w.recurrence === "Weekly"
                      ? `${w.daysOfWeek} at ${w.startTime.slice(0, 5)} for ${w.durationMinutes} min (${w.timeZoneId})`
                      : `${new Date(w.onceStartUtc!).toLocaleString()} – ${new Date(w.onceEndUtc!).toLocaleString()}`}
                  </TD>
                  <TD>
                    <Badge tone={!w.isEnabled ? "neutral" : w.isOpenNow ? "success" : "brand"}>{!w.isEnabled ? "Disabled" : w.isOpenNow ? "Open" : "Closed"}</Badge>
                  </TD>
                  <TD className="text-muted-foreground">{w.nextOccurrenceStartsUtc ? new Date(w.nextOccurrenceStartsUtc).toLocaleString() : "—"}</TD>
                  {isAdmin ? (
                    <TD className="text-right">
                      <Button
                        variant="ghost"
                        size="icon"
                        className="text-destructive"
                        aria-label={`Delete ${w.name}`}
                        onClick={async () => {
                          try {
                            await api.delete(`maintenance-windows/${w.id}`);
                            await reload();
                          } catch (e) {
                            toast({ tone: "error", title: "Could not delete window", description: messageFrom(e) });
                          }
                        }}
                      >
                        <Trash2 className="h-4 w-4" />
                      </Button>
                    </TD>
                  ) : null}
                </TR>
              ))
            )}
          </TBody>
        </Table>
      </Card>
      <CreateWindowDialog open={creating} onClose={() => setCreating(false)} onCreated={reload} />
    </div>
  );
}

function CreateWindowDialog({ open, onClose, onCreated }: { open: boolean; onClose: () => void; onCreated: () => Promise<void> }) {
  const { toast } = useToast();
  const [name, setName] = React.useState("Weekend patching");
  const [timeZoneId, setTimeZoneId] = React.useState(() => Intl.DateTimeFormat().resolvedOptions().timeZone || "UTC");
  const [selected, setSelected] = React.useState<Set<string>>(new Set(["Saturday"]));
  const [start, setStart] = React.useState("22:00");
  const [duration, setDuration] = React.useState(240);
  const [errors, setErrors] = React.useState<Record<string, string>>({});
  const [busy, setBusy] = React.useState(false);

  async function submit() {
    setBusy(true);
    try {
      await api.post("maintenance-windows", {
        name,
        description: null,
        recurrence: "Weekly",
        timeZoneId,
        daysOfWeek: [...selected].join(", "),
        startTime: `${start}:00`,
        durationMinutes: duration,
        onceStartUtc: null,
        onceEndUtc: null,
        isEnabled: true,
      });
      onClose();
      await onCreated();
    } catch (e) {
      setErrors(fieldErrorsFrom(e));
      toast({ tone: "error", title: "Could not create window", description: messageFrom(e) });
    } finally {
      setBusy(false);
    }
  }

  return (
    <Dialog
      open={open}
      onClose={onClose}
      title="New weekly maintenance window"
      footer={
        <>
          <Button variant="outline" onClick={onClose}>
            Cancel
          </Button>
          <Button loading={busy} onClick={submit} disabled={selected.size === 0}>
            Create
          </Button>
        </>
      }
    >
      <div className="space-y-3">
        <Field label="Name" htmlFor="mw-name" error={errors.name} required>
          <Input id="mw-name" value={name} onChange={(e) => setName(e.target.value)} />
        </Field>
        <Field label="Time zone (IANA)" htmlFor="mw-tz" error={errors.timeZoneId}>
          <Input id="mw-tz" value={timeZoneId} onChange={(e) => setTimeZoneId(e.target.value)} />
        </Field>
        <fieldset>
          <legend className="mb-1 text-sm font-medium">Days</legend>
          <div className="flex flex-wrap gap-3">
            {days.map((d) => (
              <label key={d} className="flex items-center gap-1 text-sm">
                <Checkbox
                  checked={selected.has(d)}
                  onChange={(e) =>
                    setSelected((s) => {
                      const next = new Set(s);
                      if (e.target.checked) next.add(d);
                      else next.delete(d);
                      return next;
                    })
                  }
                />
                {d.slice(0, 3)}
              </label>
            ))}
          </div>
        </fieldset>
        <div className="grid grid-cols-2 gap-3">
          <Field label="Starts at (local)" htmlFor="mw-start">
            <Input id="mw-start" type="time" value={start} onChange={(e) => setStart(e.target.value)} />
          </Field>
          <Field label="Duration (minutes)" htmlFor="mw-duration" error={errors.durationMinutes}>
            <NumberInput id="mw-duration" min={15} max={1440} value={duration} onValue={setDuration} />
          </Field>
        </div>
      </div>
    </Dialog>
  );
}

"use client";

import * as React from "react";
import { cn } from "@/lib/utils";
import { Card, CardContent } from "@/components/ui/card";
import { Dialog } from "@/components/ui/dialog";
import { Button } from "@/components/ui/button";
import { Field } from "@/components/ui/field";
import { Input, Select, Textarea, Checkbox } from "@/components/ui/input";
import type { TargetDto, TargetType } from "@/lib/api/endpoint-types";

export function StatTile({ label, value, hint, tone = "neutral" }: { label: string; value: React.ReactNode; hint?: string; tone?: "neutral" | "success" | "warning" | "danger" }) {
  const accent = {
    neutral: "text-foreground",
    success: "text-success",
    warning: "text-warning",
    danger: "text-destructive",
  }[tone];
  return (
    <Card>
      <CardContent className="p-4">
        <p className="text-xs font-medium uppercase tracking-wide text-muted-foreground">{label}</p>
        <p className={cn("mt-1 text-2xl font-semibold tabular-nums", accent)}>{value}</p>
        {hint ? <p className="mt-0.5 text-xs text-muted-foreground">{hint}</p> : null}
      </CardContent>
    </Card>
  );
}

/** Stacked bar of deployment outcomes. */
export function ProgressBar({ segments, label }: { segments: { value: number; className: string; title: string }[]; label: string }) {
  const total = segments.reduce((sum, s) => sum + s.value, 0);
  return (
    <div role="img" aria-label={label} className="flex h-2 w-full overflow-hidden rounded-full bg-muted">
      {total === 0
        ? null
        : segments
            .filter((s) => s.value > 0)
            .map((s) => <span key={s.title} title={`${s.title}: ${s.value}`} className={s.className} style={{ width: `${(s.value / total) * 100}%` }} />)}
    </div>
  );
}

/** Confirmation dialog that can require a written reason (used for overrides and destructive approvals). */
export function ConfirmDialog({
  open,
  title,
  description,
  confirmLabel,
  requireReason = false,
  acknowledgement,
  destructive = false,
  busy,
  onConfirm,
  onClose,
}: {
  open: boolean;
  title: string;
  description?: string;
  confirmLabel: string;
  requireReason?: boolean;
  acknowledgement?: string;
  destructive?: boolean;
  busy?: boolean;
  onConfirm: (reason: string, acknowledged: boolean) => void;
  onClose: () => void;
}) {
  const [reason, setReason] = React.useState("");
  const [acknowledged, setAcknowledged] = React.useState(false);
  const disabled = (requireReason && reason.trim().length === 0) || (acknowledgement !== undefined && !acknowledged);
  return (
    <Dialog
      open={open}
      onClose={onClose}
      title={title}
      description={description}
      footer={
        <>
          <Button variant="outline" onClick={onClose}>
            Cancel
          </Button>
          <Button variant={destructive ? "destructive" : "primary"} disabled={disabled} loading={busy} onClick={() => onConfirm(reason.trim(), acknowledged)}>
            {confirmLabel}
          </Button>
        </>
      }
    >
      <div className="space-y-3">
        <Field label={requireReason ? "Reason (recorded in the audit log)" : "Note (optional)"} htmlFor="confirm-reason" required={requireReason}>
          <Textarea id="confirm-reason" rows={3} value={reason} onChange={(e) => setReason(e.target.value)} />
        </Field>
        {acknowledgement ? (
          <label className="flex items-start gap-2 text-sm">
            <Checkbox checked={acknowledged} onChange={(e) => setAcknowledged(e.target.checked)} />
            <span>{acknowledgement}</span>
          </label>
        ) : null}
      </div>
    </Dialog>
  );
}

export interface TargetOptions {
  branches: { id: string; name: string }[];
  departments: { id: string; name: string }[];
  groups: { id: string; name: string }[];
  devices: { id: string; hostname: string }[];
}

export const targetTypeLabels: Record<TargetType, string> = {
  AllDevices: "All devices",
  Branch: "Branch",
  Department: "Department (incl. sub-departments)",
  DeviceGroup: "Device group",
  Device: "Single device",
};

export function describeTarget(target: Pick<TargetDto, "targetType" | "targetId" | "isExclusion">, options: TargetOptions): string {
  const name = (() => {
    switch (target.targetType) {
      case "AllDevices":
        return "All devices";
      case "Branch":
        return options.branches.find((b) => b.id === target.targetId)?.name ?? "Branch";
      case "Department":
        return options.departments.find((d) => d.id === target.targetId)?.name ?? "Department";
      case "DeviceGroup":
        return options.groups.find((g) => g.id === target.targetId)?.name ?? "Device group";
      default:
        return options.devices.find((d) => d.id === target.targetId)?.hostname ?? "Device";
    }
  })();
  return target.isExclusion ? `Except ${name}` : name;
}

/** Picks a target type and the matching entity. */
export function TargetPicker({
  value,
  onChange,
  options,
  idPrefix,
  allowExclusion = true,
}: {
  value: TargetDto;
  onChange: (value: TargetDto) => void;
  options: TargetOptions;
  idPrefix: string;
  allowExclusion?: boolean;
}) {
  const choices: { id: string; name: string }[] =
    value.targetType === "Branch"
      ? options.branches
      : value.targetType === "Department"
        ? options.departments
        : value.targetType === "DeviceGroup"
          ? options.groups
          : value.targetType === "Device"
            ? options.devices.map((d) => ({ id: d.id, name: d.hostname }))
            : [];
  return (
    <div className="grid gap-2 sm:grid-cols-[1fr_1fr_auto] sm:items-end">
      <Field label="Target" htmlFor={`${idPrefix}-type`}>
        <Select
          id={`${idPrefix}-type`}
          value={value.targetType}
          onChange={(e) => onChange({ targetType: e.target.value as TargetType, targetId: null, isExclusion: value.isExclusion })}
        >
          {(Object.keys(targetTypeLabels) as TargetType[]).map((t) => (
            <option key={t} value={t}>
              {targetTypeLabels[t]}
            </option>
          ))}
        </Select>
      </Field>
      {value.targetType === "AllDevices" ? (
        <div />
      ) : (
        <Field label="Which" htmlFor={`${idPrefix}-id`}>
          <Select id={`${idPrefix}-id`} value={value.targetId ?? ""} onChange={(e) => onChange({ ...value, targetId: e.target.value || null })}>
            <option value="">Select…</option>
            {choices.map((c) => (
              <option key={c.id} value={c.id}>
                {c.name}
              </option>
            ))}
          </Select>
        </Field>
      )}
      {allowExclusion ? (
        <label className="flex h-9 items-center gap-2 text-sm">
          <Checkbox checked={value.isExclusion} onChange={(e) => onChange({ ...value, isExclusion: e.target.checked })} />
          Exclude
        </label>
      ) : null}
    </div>
  );
}

export function NumberInput(props: Omit<React.InputHTMLAttributes<HTMLInputElement>, "type" | "onChange" | "value"> & { value: number; onValue: (v: number) => void }) {
  const { value, onValue, ...rest } = props;
  return <Input type="number" value={Number.isFinite(value) ? value : ""} onChange={(e) => onValue(e.target.valueAsNumber)} {...rest} />;
}

/** The time of first render; stable across re-renders (render must stay pure). */
export function useNow(): number {
  const [now] = React.useState(() => Date.now());
  return now;
}

export function formatRelative(iso: string | null | undefined, now: number): string {
  if (!iso) return "—";
  const diff = now - new Date(iso).getTime();
  const minutes = Math.round(Math.abs(diff) / 60000);
  const suffix = diff >= 0 ? "ago" : "from now";
  if (minutes < 1) return "just now";
  if (minutes < 60) return `${minutes} min ${suffix}`;
  const hours = Math.round(minutes / 60);
  if (hours < 48) return `${hours} h ${suffix}`;
  return `${Math.round(hours / 24)} d ${suffix}`;
}

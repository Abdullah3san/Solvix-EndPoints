"use client";

import * as React from "react";
import { Apple, Download, Monitor, Terminal } from "lucide-react";
import { api, downloadFile } from "@/lib/api/client";
import type { BranchDto, CreatedEnrollmentTokenDto, DepartmentDto, DeviceGroupDto, PagedResult } from "@/lib/api/types";
import { fieldErrorsFrom, messageFrom } from "@/lib/forms";
import { cn, formatDate } from "@/lib/utils";
import { Alert } from "@/components/ui/alert";
import { Badge } from "@/components/ui/badge";
import { Button, type ButtonProps } from "@/components/ui/button";
import { Dialog } from "@/components/ui/dialog";
import { ErrorState } from "@/components/ui/empty-state";
import { Field } from "@/components/ui/field";
import { Input, Select } from "@/components/ui/input";
import { Skeleton } from "@/components/ui/skeleton";
import { useCan } from "@/components/providers/session-provider";
import { useToast } from "@/components/providers/toast-provider";

const LIFETIMES = [
  { label: "1 hour", hours: 1 },
  { label: "8 hours", hours: 8 },
  { label: "24 hours", hours: 24 },
  { label: "3 days", hours: 72 },
  { label: "7 days", hours: 168 },
];

/** Platforms offered in the dialog. Only Windows has an agent today; the others are shown so the roadmap is visible. */
const PLATFORMS = [
  { id: "windows", label: "Windows", icon: Monitor, available: true },
  { id: "macos", label: "macOS", icon: Apple, available: false },
  { id: "linux", label: "Linux", icon: Terminal, available: false },
] as const;

interface Options {
  branches: BranchDto[];
  departments: DepartmentDto[];
  groups: DeviceGroupDto[];
}

/** "Download agent" button for administrators. Opens the guided dialog; renders nothing for other roles. */
export function DownloadAgentButton({ variant = "primary", size = "md", label = "Download agent", defaultBranchId }: { variant?: ButtonProps["variant"]; size?: ButtonProps["size"]; label?: string; defaultBranchId?: string }) {
  const { isAdmin } = useCan();
  const [open, setOpen] = React.useState(false);
  if (!isAdmin) return null;
  return (
    <>
      <Button variant={variant} size={size} onClick={() => setOpen(true)}>
        <Download /> {label}
      </Button>
      {open ? <DownloadAgentDialog defaultBranchId={defaultBranchId} onClose={() => setOpen(false)} /> : null}
    </>
  );
}

/**
 * One step from "I need the agent for this office" to the installer: creates a short-lived enrollment token
 * with the chosen placement and downloads the ready-to-run package. Tokens are listed (and revocable) under
 * Endpoints → Enrollment.
 */
export function DownloadAgentDialog({ onClose, defaultBranchId = "" }: { onClose: () => void; defaultBranchId?: string }) {
  const { toast } = useToast();
  const [options, setOptions] = React.useState<Options | null>(null);
  const [loadError, setLoadError] = React.useState<string | null>(null);
  const [platform, setPlatform] = React.useState<(typeof PLATFORMS)[number]["id"]>("windows");
  const [branchId, setBranchId] = React.useState(defaultBranchId);
  const [departmentId, setDepartmentId] = React.useState("");
  const [groupId, setGroupId] = React.useState("");
  const [lifetimeHours, setLifetimeHours] = React.useState(24);
  const [maxUses, setMaxUses] = React.useState(10);
  const [pending, setPending] = React.useState(false);
  const [errors, setErrors] = React.useState<Record<string, string>>({});
  const [formError, setFormError] = React.useState<string | null>(null);
  const [done, setDone] = React.useState<{ file: string; token: CreatedEnrollmentTokenDto["enrollmentToken"] } | null>(null);

  const loadOptions = React.useCallback(async () => {
    setLoadError(null);
    try {
      const [branches, departments, groups] = await Promise.all([
        api.get<PagedResult<BranchDto>>("branches", { pageSize: 200 }),
        api.get<PagedResult<DepartmentDto>>("departments", { pageSize: 200 }),
        api.get<PagedResult<DeviceGroupDto>>("device-groups", { pageSize: 200 }),
      ]);
      setOptions({ branches: branches.items.filter((b) => b.isActive), departments: departments.items, groups: groups.items });
    } catch (e) {
      setLoadError(messageFrom(e, "The branches could not be loaded."));
    }
  }, []);

  React.useEffect(() => {
    // eslint-disable-next-line react-hooks/set-state-in-effect
    void loadOptions();
  }, [loadOptions]);

  async function submit(event: React.FormEvent) {
    event.preventDefault();
    setPending(true);
    setFormError(null);
    setErrors({});
    let tokenId: string | null = null;
    try {
      const created = await api.post<CreatedEnrollmentTokenDto>("enrollment-tokens", {
        name: `Agent download ${new Date().toLocaleString()}`,
        lifetimeHours,
        maxUses,
        deviceGroupId: groupId || null,
        branchId: branchId || null,
        departmentId: departmentId || null,
      });
      tokenId = created.enrollmentToken.id;
      const file = await downloadFile("enrollment-tokens/package", { token: created.token }, "SolvixAgent.zip");
      setDone({ file, token: created.enrollmentToken });
    } catch (e) {
      // No package was delivered, so the token we just minted is useless: revoke it instead of leaving it active.
      if (tokenId) await api.post(`enrollment-tokens/${tokenId}/revoke`).catch(() => undefined);
      setErrors(fieldErrorsFrom(e));
      setFormError(messageFrom(e, "The agent package could not be created."));
      toast({ tone: "error", title: "Could not download the agent", description: messageFrom(e) });
    } finally {
      setPending(false);
    }
  }

  const departments = options?.departments.filter((d) => d.branchId === branchId) ?? [];

  return (
    <Dialog
      open
      onClose={() => (pending ? undefined : onClose())}
      title="Download agent"
      description="Creates a short-lived enrollment token for the chosen office and downloads the installer package."
      className="max-w-xl"
      footer={
        done ? (
          <Button onClick={onClose}>Done</Button>
        ) : (
          <>
            <Button variant="outline" onClick={onClose} disabled={pending}>
              Cancel
            </Button>
            <Button type="submit" form="download-agent-form" loading={pending} disabled={!options || platform !== "windows"}>
              <Download /> Download agent
            </Button>
          </>
        )
      }
    >
      {loadError ? (
        <ErrorState size="sm" description={loadError} onRetry={() => void loadOptions()} />
      ) : !options ? (
        <div className="space-y-3" aria-busy="true" aria-label="Loading">
          <Skeleton className="h-9 w-full" />
          <Skeleton className="h-9 w-full" />
          <Skeleton className="h-9 w-full" />
        </div>
      ) : done ? (
        <div className="space-y-3">
          <Alert tone="success" title={`Downloaded ${done.file}`}>
            Extract the whole zip on the Windows computer, run <span className="font-mono">Solvix.Agent.exe</span> and approve the administrator prompt. The computer
            appears under Endpoints → Devices within a minute.
          </Alert>
          <Alert>
            Valid until {formatDate(done.token.expiresAtUtc)} for up to {done.token.maxUses} computer(s)
            {[done.token.branchName, done.token.departmentName].filter(Boolean).length > 0 ? `, assigned to ${[done.token.branchName, done.token.departmentName].filter(Boolean).join(" / ")}` : ""}.
            Treat the package like a password; revoke it under Endpoints → Enrollment when the rollout is complete.
          </Alert>
        </div>
      ) : (
        <form id="download-agent-form" onSubmit={submit} className="space-y-4">
          {formError ? <Alert tone="error">{formError}</Alert> : null}
          <div role="radiogroup" aria-label="Platform" className="space-y-1.5">
            <p className="text-sm font-medium">Platform</p>
            <div className="flex flex-wrap gap-2">
              {PLATFORMS.map((p) => (
                <button
                  key={p.id}
                  type="button"
                  role="radio"
                  aria-checked={platform === p.id}
                  disabled={!p.available}
                  onClick={() => setPlatform(p.id)}
                  className={cn(
                    "inline-flex h-9 items-center gap-2 rounded-full border px-3.5 text-sm transition-colors disabled:cursor-not-allowed disabled:opacity-60",
                    platform === p.id ? "border-primary bg-accent font-medium text-accent-foreground" : "border-input bg-card hover:bg-muted",
                  )}
                >
                  <p.icon className="size-4" aria-hidden /> {p.label}
                  {p.available ? null : <Badge tone="neutral">Not available yet</Badge>}
                </button>
              ))}
            </div>
            <p className="text-xs text-muted-foreground">Windows 10/11 and Windows Server 2016–2025 (64-bit). macOS and Linux agents are planned.</p>
          </div>
          <div className="grid gap-4 sm:grid-cols-2">
            <Field label="Branch" htmlFor="dl-branch" error={errors.branchId} hint="Computers that enroll are assigned here.">
              <Select
                id="dl-branch"
                value={branchId}
                onChange={(e) => {
                  setBranchId(e.target.value);
                  setDepartmentId("");
                }}
              >
                <option value="">No branch</option>
                {options.branches.map((b) => (
                  <option key={b.id} value={b.id}>
                    {b.name} ({b.code})
                  </option>
                ))}
              </Select>
            </Field>
            <Field label="Department" htmlFor="dl-department" error={errors.departmentId}>
              <Select id="dl-department" value={departmentId} onChange={(e) => setDepartmentId(e.target.value)} disabled={!branchId}>
                <option value="">{branchId ? "No department" : "Select a branch first"}</option>
                {departments.map((d) => (
                  <option key={d.id} value={d.id}>
                    {d.name}
                  </option>
                ))}
              </Select>
            </Field>
          </div>
          <Field label="Add computers to group" htmlFor="dl-group">
            <Select id="dl-group" value={groupId} onChange={(e) => setGroupId(e.target.value)}>
              <option value="">No group</option>
              {options.groups.map((g) => (
                <option key={g.id} value={g.id}>
                  {g.name}
                </option>
              ))}
            </Select>
          </Field>
          <div className="grid gap-4 sm:grid-cols-2">
            <Field label="Valid for" htmlFor="dl-lifetime" error={errors.lifetimeHours}>
              <Select id="dl-lifetime" value={lifetimeHours} onChange={(e) => setLifetimeHours(Number(e.target.value))}>
                {LIFETIMES.map((l) => (
                  <option key={l.hours} value={l.hours}>
                    {l.label}
                  </option>
                ))}
              </Select>
            </Field>
            <Field label="Number of computers" htmlFor="dl-uses" error={errors.maxUses} hint="The package stops working after this many installs.">
              <Input id="dl-uses" type="number" min={1} max={10000} value={maxUses} onChange={(e) => setMaxUses(Number(e.target.value))} />
            </Field>
          </div>
        </form>
      )}
    </Dialog>
  );
}

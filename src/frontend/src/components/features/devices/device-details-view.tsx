"use client";

import * as React from "react";
import { useRouter } from "next/navigation";
import { Archive, Pencil, RefreshCw, Trash2 } from "lucide-react";
import { api } from "@/lib/api/client";
import type { BranchDto, DepartmentDto, DeviceDetailsDto, DeviceGroupDto, DeviceInventoryDto, DeviceMonitoringDto, MemberDto } from "@/lib/api/types";
import { connectivityTone, deviceLabel, formatBytes, formatRelative, healthTone } from "@/lib/devices";
import { emptyToNull, fieldErrorsFrom, messageFrom } from "@/lib/forms";
import { formatDate } from "@/lib/utils";
import { Alert } from "@/components/ui/alert";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Dialog } from "@/components/ui/dialog";
import { Field } from "@/components/ui/field";
import { Checkbox, Input, Select, Textarea } from "@/components/ui/input";
import { PageHeader } from "@/components/ui/page-header";
import { Table, THead, TBody, TR, TH, TD, EmptyRow } from "@/components/ui/table";
import { DeviceMonitoringPanel } from "@/components/features/monitoring/device-monitoring-panel";
import { DeviceAssetCard } from "@/components/features/assets/device-asset-card";
import { useToast } from "@/components/providers/toast-provider";
import { useCan } from "@/components/providers/session-provider";

const tabs = ["Overview", "Monitoring", "Hardware", "Network", "Storage", "Software", "Security"] as const;
type Tab = (typeof tabs)[number];

interface Props {
  initial: DeviceDetailsDto;
  inventory: DeviceInventoryDto | null;
  groups: DeviceGroupDto[];
  members: MemberDto[];
  branches: BranchDto[];
  departments: DepartmentDto[];
  monitoring?: DeviceMonitoringDto | null;
}

export function DeviceDetailsView({ initial, inventory, groups, members, branches, departments, monitoring = null }: Props) {
  const router = useRouter();
  const { toast } = useToast();
  const { isAdmin } = useCan();
  const [device, setDevice] = React.useState(initial);
  const [tab, setTab] = React.useState<Tab>("Overview");
  const [dialog, setDialog] = React.useState<"edit" | "assign" | "groups" | "retire" | "delete" | null>(null);
  const [busy, setBusy] = React.useState(false);
  const s = device.summary;
  const report = inventory?.report ?? null;

  async function act(action: () => Promise<DeviceDetailsDto | void>, success: string) {
    setBusy(true);
    try {
      const result = await action();
      if (result) setDevice(result);
      toast({ tone: "success", title: success });
      setDialog(null);
      router.refresh();
    } catch (e) {
      toast({ tone: "error", title: "Action failed", description: messageFrom(e) });
    } finally {
      setBusy(false);
    }
  }

  return (
    <>
      <PageHeader
        title={deviceLabel(s)}
        description={[s.hostname !== deviceLabel(s) ? s.hostname : null, s.osName, [s.manufacturer, s.model].filter(Boolean).join(" ")].filter(Boolean).join(" · ")}
        actions={
          isAdmin && s.status === "Active" ? (
            <>
              <Button variant="outline" onClick={() => act(() => api.post<DeviceDetailsDto>(`devices/${s.id}/inventory-request`), "Inventory requested")} loading={busy} disabled={device.inventoryRequested}>
                <RefreshCw className="h-4 w-4" /> {device.inventoryRequested ? "Inventory requested" : "Refresh inventory"}
              </Button>
              <Button variant="outline" onClick={() => setDialog("edit")}>
                <Pencil className="h-4 w-4" /> Edit
              </Button>
              <Button variant="outline" onClick={() => setDialog("retire")}>
                <Archive className="h-4 w-4" /> Retire
              </Button>
            </>
          ) : isAdmin ? (
            <Button variant="destructive" onClick={() => setDialog("delete")}>
              <Trash2 className="h-4 w-4" /> Delete record
            </Button>
          ) : null
        }
      />

      <div className="mb-4 flex flex-wrap items-center gap-2">
        <Badge tone={connectivityTone(s.status, s.connectivity)}>{s.status === "Retired" ? "Retired" : s.connectivity}</Badge>
        {s.healthStatus ? <Badge tone={healthTone(s.healthStatus)}>Agent {s.healthStatus.toLowerCase()}</Badge> : null}
        <Badge tone={s.agentUpdateAvailable ? "warning" : "neutral"}>
          Agent {s.agentVersion}
          {s.agentUpdateAvailable ? " · update available" : ""}
        </Badge>
        <span className="text-sm text-muted-foreground">Last seen {formatRelative(s.lastSeenAtUtc)}</span>
      </div>

      {device.health?.issues.length ? (
        <Alert tone={s.healthStatus === "Unhealthy" ? "error" : "info"} title="Agent reported issues" className="mb-4">
          <ul className="list-inside list-disc">
            {device.health.issues.map((i) => (
              <li key={i.component}>
                <span className="font-medium">{i.component}</span>: {i.message}
              </li>
            ))}
          </ul>
        </Alert>
      ) : null}

      <div role="tablist" aria-label="Device sections" className="mb-4 flex gap-1 overflow-x-auto border-b border-border">
        {tabs.map((t) => (
          <button
            key={t}
            role="tab"
            aria-selected={tab === t}
            onClick={() => setTab(t)}
            className={`-mb-px border-b-2 px-3 py-2 text-sm font-medium ${tab === t ? "border-primary text-foreground" : "border-transparent text-muted-foreground hover:text-foreground"}`}
          >
            {t}
          </button>
        ))}
      </div>

      {tab === "Monitoring" ? <DeviceMonitoringPanel deviceId={s.id} initial={monitoring} /> : null}

      {!report && tab !== "Overview" && tab !== "Security" && tab !== "Monitoring" ? (
        <Alert>No inventory has been reported yet. The agent uploads it shortly after enrollment.</Alert>
      ) : null}

      {tab === "Overview" ? (
        <div className="grid gap-4 lg:grid-cols-2">
          <InfoCard
            title="Device"
            rows={[
              ["Hostname", s.hostname],
              ["Domain", device.domainName],
              ["Serial number", s.serialNumber],
              ["Manufacturer / model", [s.manufacturer, s.model].filter(Boolean).join(" ")],
              ["Processor", device.processorName],
              ["Memory", formatBytes(device.totalMemoryBytes)],
              ["Last boot", formatDate(device.lastBootAtUtc)],
              ["Notes", device.notes],
            ]}
          />
          <InfoCard
            title="Operating system"
            rows={[
              ["Name", s.osName],
              ["Version", [s.osDisplayVersion, s.osVersion].filter(Boolean).join(" · ")],
              ["Build", s.osBuild],
              ["Architecture", device.architecture],
              ["Supported", report?.operatingSystem?.isSupported === undefined ? null : report.operatingSystem.isSupported ? "Yes" : "No"],
              ["Installed", formatDate(report?.operatingSystem?.installDateUtc)],
            ]}
          />
          <Card>
            <CardHeader className="flex-row items-start justify-between">
              <div>
                <CardTitle>Assignment</CardTitle>
                <CardDescription>Primary user and organisational unit.</CardDescription>
              </div>
              {isAdmin ? (
                <Button size="sm" variant="outline" onClick={() => setDialog("assign")}>
                  Change
                </Button>
              ) : null}
            </CardHeader>
            <CardContent className="space-y-2 text-sm">
              <Row label="User" value={device.assignment.user ? `${device.assignment.user.displayName ?? "Former member"}${device.assignment.user.email ? ` (${device.assignment.user.email})` : ""}` : null} />
              <Row label="Branch" value={device.assignment.branch?.name} />
              <Row label="Department" value={device.assignment.department?.name} />
            </CardContent>
          </Card>
          <Card>
            <CardHeader className="flex-row items-start justify-between">
              <div>
                <CardTitle>Groups</CardTitle>
                <CardDescription>Static device groups.</CardDescription>
              </div>
              {isAdmin ? (
                <Button size="sm" variant="outline" onClick={() => setDialog("groups")}>
                  Edit
                </Button>
              ) : null}
            </CardHeader>
            <CardContent className="flex flex-wrap gap-1">
              {device.groups.length ? device.groups.map((g) => <Badge key={g.id} tone="brand">{g.name}</Badge>) : <span className="text-sm text-muted-foreground">Not in any group.</span>}
            </CardContent>
          </Card>
          <InfoCard
            title="Agent"
            rows={[
              ["Version", s.agentVersion],
              ["Version changed", formatDate(device.agentVersionChangedAtUtc)],
              ["Uptime", device.health ? `${Math.round(device.health.uptimeSeconds / 3600)} h` : null],
              ["Last inventory", formatDate(s.lastInventoryAtUtc)],
              ["Reconnect attempts before last contact", device.health ? String(device.health.consecutiveFailures) : null],
            ]}
          />
          {isAdmin ? <DeviceAssetCard deviceId={s.id} /> : null}
          <InfoCard
            title="Enrollment"
            rows={[
              ["First enrolled", formatDate(s.enrolledAtUtc)],
              ["Last enrolled", formatDate(device.lastEnrolledAtUtc)],
              ["Enrollments", String(device.enrollmentCount)],
              ["Retired", device.retiredAtUtc ? formatDate(device.retiredAtUtc) : null],
            ]}
          />
        </div>
      ) : null}

      {tab === "Hardware" && report ? (
        <div className="grid gap-4 lg:grid-cols-2">
          <InfoCard
            title="System"
            rows={[
              ["Manufacturer", report.system?.manufacturer],
              ["Model", report.system?.model],
              ["Serial number", report.system?.serialNumber],
              ["SMBIOS UUID", report.system?.hardwareId],
              ["SKU", report.system?.systemSku],
              ["Chassis", report.system?.chassisType],
            ]}
          />
          <InfoCard
            title="BIOS / firmware"
            rows={[
              ["Manufacturer", report.bios?.manufacturer],
              ["Version", report.bios?.version],
              ["SMBIOS", report.bios?.smbiosVersion],
              ["Release date", report.bios?.releaseDate ? new Date(report.bios.releaseDate).toLocaleDateString() : null],
              ["Firmware", report.bios?.firmwareType],
              ["Secure Boot", report.bios?.secureBootEnabled === undefined ? null : report.bios.secureBootEnabled ? "Enabled" : "Disabled"],
            ]}
          />
          <SimpleTable
            title="Processors"
            headers={["Name", "Cores", "Threads", "Clock"]}
            rows={report.processors.map((p) => [p.name, p.cores, p.logicalProcessors, p.maxClockSpeedMhz ? `${p.maxClockSpeedMhz} MHz` : null])}
          />
          <SimpleTable
            title={`Memory · ${formatBytes(report.memory?.totalPhysicalBytes)} (${formatBytes(report.memory?.availablePhysicalBytes)} free)`}
            headers={["Slot", "Capacity", "Speed", "Part number"]}
            rows={(report.memory?.modules ?? []).map((m) => [m.slot, formatBytes(m.capacityBytes), m.speedMhz ? `${m.speedMhz} MHz` : null, m.partNumber])}
          />
        </div>
      ) : null}

      {tab === "Network" && report ? (
        <SimpleTable
          title="Network adapters"
          headers={["Name", "MAC address", "Status", "IPv4", "IPv6", "Gateway", "DHCP"]}
          rows={report.networkAdapters.map((a) => [
            <span key="n">
              {a.name}
              {a.isPhysical ? null : <Badge className="ml-1">virtual</Badge>}
            </span>,
            a.macAddress,
            a.status,
            a.iPv4Addresses.join(", "),
            a.iPv6Addresses.join(", "),
            a.gateways.join(", "),
            a.dhcpEnabled === undefined ? null : a.dhcpEnabled ? "Yes" : "No",
          ])}
        />
      ) : null}

      {tab === "Storage" && report ? (
        <div className="grid gap-4">
          <SimpleTable
            title="Physical disks"
            headers={["Model", "Size", "Media", "Bus", "Health", "Serial"]}
            rows={report.disks.map((d) => [d.model, formatBytes(d.sizeBytes), d.mediaType, d.busType, d.healthStatus, d.serialNumber])}
          />
          <SimpleTable
            title="Volumes"
            headers={["Drive", "Label", "File system", "Size", "Free", "Type"]}
            rows={report.volumes.map((v) => [v.driveLetter, v.label, v.fileSystem, formatBytes(v.sizeBytes), formatBytes(v.freeBytes), v.driveType])}
          />
        </div>
      ) : null}

      {tab === "Software" && report ? <SoftwareTable software={report.software} /> : null}

      {tab === "Security" ? (
        <SimpleTable
          title="Device credentials (X.509 client certificates)"
          headers={["SHA-256 thumbprint", "Issued", "Expires", "First used", "State"]}
          rows={device.credentials.map((c) => [
            <span key="t" className="font-mono text-xs" title={c.thumbprint}>
              {c.thumbprint.slice(0, 16)}…
            </span>,
            formatDate(c.issuedAtUtc),
            formatDate(c.notAfterUtc),
            formatDate(c.firstUsedAtUtc),
            c.revokedAtUtc ? <Badge key="s" tone="neutral">Revoked · {c.revocationReason}</Badge> : <Badge key="s" tone="success">Active</Badge>,
          ])}
        />
      ) : null}

      {dialog === "edit" ? <EditDialog device={device} onClose={() => setDialog(null)} onSaved={setDevice} /> : null}
      {dialog === "assign" ? <AssignDialog device={device} members={members} branches={branches} departments={departments} onClose={() => setDialog(null)} onSaved={setDevice} /> : null}
      {dialog === "groups" ? <GroupsDialog device={device} groups={groups} onClose={() => setDialog(null)} onSaved={setDevice} /> : null}
      <Dialog
        open={dialog === "retire"}
        onClose={() => setDialog(null)}
        title="Retire device?"
        description="All device credentials are revoked immediately; the agent can no longer report. The record is kept for reference and the machine cannot re-enroll until the record is deleted."
        footer={
          <>
            <Button variant="outline" onClick={() => setDialog(null)}>
              Cancel
            </Button>
            <Button variant="destructive" loading={busy} onClick={() => act(() => api.post<DeviceDetailsDto>(`devices/${s.id}/retire`), "Device retired")}>
              Retire
            </Button>
          </>
        }
      >
        <p className="text-sm text-muted-foreground">{deviceLabel(s)}</p>
      </Dialog>
      <Dialog
        open={dialog === "delete"}
        onClose={() => setDialog(null)}
        title="Delete device record?"
        description="The record, its inventory and credentials are deleted. The machine can then enroll again as a new device."
        footer={
          <>
            <Button variant="outline" onClick={() => setDialog(null)}>
              Cancel
            </Button>
            <Button
              variant="destructive"
              loading={busy}
              onClick={async () => {
                await act(() => api.delete(`devices/${s.id}`), "Device deleted");
                router.push("/endpoints/devices");
              }}
            >
              Delete
            </Button>
          </>
        }
      >
        <p className="text-sm text-muted-foreground">This action cannot be undone.</p>
      </Dialog>
    </>
  );
}

function Row({ label, value }: { label: string; value: string | null | undefined }) {
  return (
    <div className="flex justify-between gap-4 py-1">
      <span className="text-muted-foreground">{label}</span>
      <span className="text-right">{value && value !== "—" ? value : "—"}</span>
    </div>
  );
}

function InfoCard({ title, rows }: { title: string; rows: [string, string | null | undefined][] }) {
  return (
    <Card>
      <CardHeader>
        <CardTitle>{title}</CardTitle>
      </CardHeader>
      <CardContent className="space-y-1 text-sm">
        {rows.map(([label, value]) => (
          <Row key={label} label={label} value={value} />
        ))}
      </CardContent>
    </Card>
  );
}

function SimpleTable({ title, headers, rows }: { title: string; headers: string[]; rows: React.ReactNode[][] }) {
  return (
    <Card>
      <CardHeader>
        <CardTitle>{title}</CardTitle>
      </CardHeader>
      <Table>
        <THead>
          <TR>
            {headers.map((h) => (
              <TH key={h}>{h}</TH>
            ))}
          </TR>
        </THead>
        <TBody>
          {rows.length === 0 ? (
            <EmptyRow colSpan={headers.length}>Nothing reported.</EmptyRow>
          ) : (
            rows.map((cells, i) => (
              <TR key={i}>
                {cells.map((cell, j) => (
                  <TD key={j} className={j === 0 ? "font-medium" : "text-muted-foreground"}>
                    {cell === null || cell === undefined || cell === "" ? "—" : cell}
                  </TD>
                ))}
              </TR>
            ))
          )}
        </TBody>
      </Table>
    </Card>
  );
}

function SoftwareTable({ software }: { software: DeviceInventoryDto["report"]["software"] }) {
  const [filter, setFilter] = React.useState("");
  const term = filter.trim().toLowerCase();
  const visible = term ? software.filter((a) => `${a.name} ${a.publisher ?? ""}`.toLowerCase().includes(term)) : software;
  return (
    <div className="space-y-3">
      <Input placeholder={`Filter ${software.length} applications…`} value={filter} onChange={(e) => setFilter(e.target.value)} className="max-w-sm" aria-label="Filter applications" />
      <SimpleTable
        title={`Installed applications (${visible.length})`}
        headers={["Name", "Version", "Publisher", "Scope", "Installed"]}
        rows={visible.map((a) => [a.name, a.version, a.publisher, a.scope, a.installDate])}
      />
    </div>
  );
}

function EditDialog({ device, onClose, onSaved }: { device: DeviceDetailsDto; onClose: () => void; onSaved: (d: DeviceDetailsDto) => void }) {
  const { toast } = useToast();
  const [displayName, setDisplayName] = React.useState(device.summary.displayName ?? "");
  const [notes, setNotes] = React.useState(device.notes ?? "");
  const [errors, setErrors] = React.useState<Record<string, string>>({});
  const [pending, setPending] = React.useState(false);

  async function submit(event: React.FormEvent) {
    event.preventDefault();
    setPending(true);
    try {
      onSaved(await api.put<DeviceDetailsDto>(`devices/${device.summary.id}`, { displayName: emptyToNull(displayName), notes: emptyToNull(notes) }));
      toast({ tone: "success", title: "Device updated" });
      onClose();
    } catch (e) {
      setErrors(fieldErrorsFrom(e));
    } finally {
      setPending(false);
    }
  }

  return (
    <Dialog
      open
      onClose={onClose}
      title="Edit device"
      footer={
        <>
          <Button variant="outline" onClick={onClose}>
            Cancel
          </Button>
          <Button type="submit" form="device-edit" loading={pending}>
            Save
          </Button>
        </>
      }
    >
      <form id="device-edit" onSubmit={submit} className="space-y-4">
        <Field label="Display name" htmlFor="device-name" error={errors.displayName} hint="Optional friendly name; the hostname stays as reported.">
          <Input id="device-name" value={displayName} onChange={(e) => setDisplayName(e.target.value)} maxLength={200} />
        </Field>
        <Field label="Notes" htmlFor="device-notes" error={errors.notes}>
          <Textarea id="device-notes" value={notes} onChange={(e) => setNotes(e.target.value)} maxLength={2000} />
        </Field>
      </form>
    </Dialog>
  );
}

function AssignDialog({
  device,
  members,
  branches,
  departments,
  onClose,
  onSaved,
}: {
  device: DeviceDetailsDto;
  members: MemberDto[];
  branches: BranchDto[];
  departments: DepartmentDto[];
  onClose: () => void;
  onSaved: (d: DeviceDetailsDto) => void;
}) {
  const { toast } = useToast();
  const [userId, setUserId] = React.useState(device.assignment.user?.id ?? "");
  const [branchId, setBranchId] = React.useState(device.assignment.branch?.id ?? "");
  const [departmentId, setDepartmentId] = React.useState(device.assignment.department?.id ?? "");
  const [error, setError] = React.useState<string | null>(null);
  const [pending, setPending] = React.useState(false);
  const branchDepartments = departments.filter((d) => !branchId || d.branchId === branchId);

  async function submit(event: React.FormEvent) {
    event.preventDefault();
    setPending(true);
    setError(null);
    try {
      onSaved(
        await api.put<DeviceDetailsDto>(`devices/${device.summary.id}/assignment`, {
          assignedUserId: userId || null,
          branchId: branchId || null,
          departmentId: departmentId || null,
        }),
      );
      toast({ tone: "success", title: "Assignment updated" });
      onClose();
    } catch (e) {
      setError(messageFrom(e));
    } finally {
      setPending(false);
    }
  }

  return (
    <Dialog
      open
      onClose={onClose}
      title="Assign device"
      footer={
        <>
          <Button variant="outline" onClick={onClose}>
            Cancel
          </Button>
          <Button type="submit" form="device-assign" loading={pending}>
            Save
          </Button>
        </>
      }
    >
      <form id="device-assign" onSubmit={submit} className="space-y-4">
        {error ? <Alert tone="error">{error}</Alert> : null}
        <Field label="Primary user" htmlFor="assign-user">
          <Select id="assign-user" value={userId} onChange={(e) => setUserId(e.target.value)}>
            <option value="">Unassigned</option>
            {members
              .filter((m) => m.isActive)
              .map((m) => (
                <option key={m.userId} value={m.userId}>
                  {m.displayName} ({m.email})
                </option>
              ))}
          </Select>
        </Field>
        <Field label="Branch" htmlFor="assign-branch">
          <Select
            id="assign-branch"
            value={branchId}
            onChange={(e) => {
              setBranchId(e.target.value);
              setDepartmentId("");
            }}
          >
            <option value="">None</option>
            {branches.map((b) => (
              <option key={b.id} value={b.id}>
                {b.name} ({b.code})
              </option>
            ))}
          </Select>
        </Field>
        <Field label="Department" htmlFor="assign-department">
          <Select id="assign-department" value={departmentId} onChange={(e) => setDepartmentId(e.target.value)}>
            <option value="">None</option>
            {branchDepartments.map((d) => (
              <option key={d.id} value={d.id}>
                {d.name} · {d.branchName}
              </option>
            ))}
          </Select>
        </Field>
      </form>
    </Dialog>
  );
}

function GroupsDialog({ device, groups, onClose, onSaved }: { device: DeviceDetailsDto; groups: DeviceGroupDto[]; onClose: () => void; onSaved: (d: DeviceDetailsDto) => void }) {
  const { toast } = useToast();
  const [selected, setSelected] = React.useState<Set<string>>(new Set(device.groups.map((g) => g.id)));
  const [pending, setPending] = React.useState(false);

  async function save() {
    setPending(true);
    try {
      onSaved(await api.put<DeviceDetailsDto>(`devices/${device.summary.id}/groups`, { groupIds: [...selected] }));
      toast({ tone: "success", title: "Groups updated" });
      onClose();
    } catch (e) {
      toast({ tone: "error", title: "Could not update groups", description: messageFrom(e) });
    } finally {
      setPending(false);
    }
  }

  return (
    <Dialog
      open
      onClose={onClose}
      title="Device groups"
      footer={
        <>
          <Button variant="outline" onClick={onClose}>
            Cancel
          </Button>
          <Button onClick={save} loading={pending}>
            Save
          </Button>
        </>
      }
    >
      {groups.length === 0 ? (
        <p className="text-sm text-muted-foreground">No groups exist yet. Create one under Device groups.</p>
      ) : (
        <div className="space-y-2">
          {groups.map((g) => (
            <label key={g.id} className="flex items-center gap-2 text-sm">
              <Checkbox
                checked={selected.has(g.id)}
                onChange={(e) =>
                  setSelected((current) => {
                    const next = new Set(current);
                    if (e.target.checked) next.add(g.id);
                    else next.delete(g.id);
                    return next;
                  })
                }
              />
              {g.name}
            </label>
          ))}
        </div>
      )}
    </Dialog>
  );
}

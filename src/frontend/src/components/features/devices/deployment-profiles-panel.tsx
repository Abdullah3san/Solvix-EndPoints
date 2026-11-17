"use client";

import * as React from "react";
import { Download, Pencil, Plus, RefreshCw, Trash2 } from "lucide-react";
import { api } from "@/lib/api/client";
import { installerUrl } from "@/lib/api/types";
import type { BranchDto, DepartmentDto, DeploymentProfileDto, DeviceGroupDto, SaveDeploymentProfileRequest } from "@/lib/api/types";
import { fieldErrorsFrom, messageFrom } from "@/lib/forms";
import { formatDate } from "@/lib/utils";
import { Alert } from "@/components/ui/alert";
import { Badge } from "@/components/ui/badge";
import { Button, buttonClasses } from "@/components/ui/button";
import { Card } from "@/components/ui/card";
import { Dialog } from "@/components/ui/dialog";
import { Field } from "@/components/ui/field";
import { Input, Select } from "@/components/ui/input";
import { Table, THead, TBody, TR, TH, TD, EmptyRow } from "@/components/ui/table";
import { useToast } from "@/components/providers/toast-provider";

interface Props {
  profiles: DeploymentProfileDto[];
  groups: DeviceGroupDto[];
  branches: BranchDto[];
  departments: DepartmentDto[];
  onChanged: () => Promise<void>;
}

/** Deployment profiles: where approved computers land, and the installer (.exe) that belongs to each. */
export function DeploymentProfilesPanel({ profiles, groups, branches, departments, onChanged }: Props) {
  const { toast } = useToast();
  const [editing, setEditing] = React.useState<DeploymentProfileDto | "new" | null>(null);
  const [busyId, setBusyId] = React.useState<string | null>(null);

  async function run(profile: DeploymentProfileDto, action: () => Promise<unknown>, success: string) {
    setBusyId(profile.id);
    try {
      await action();
      toast({ tone: "success", title: success });
      await onChanged();
    } catch (e) {
      toast({ tone: "error", title: "Action failed", description: messageFrom(e) });
    } finally {
      setBusyId(null);
    }
  }

  return (
    <>
      <Alert className="mb-4">
        Download the installer (.exe) of a profile and run it on a Windows computer. The computer appears under <strong>Pending approval</strong>; once you approve it, it is
        placed in the profile&apos;s branch, department and group. Rotate the code to stop old installers from enrolling.
      </Alert>
      <div className="mb-4 flex justify-end">
        <Button onClick={() => setEditing("new")}>
          <Plus className="h-4 w-4" /> New profile
        </Button>
      </div>
      <Card>
        <Table>
          <THead>
            <TR>
              <TH>Profile</TH>
              <TH>State</TH>
              <TH>Places devices in</TH>
              <TH>Waiting / enrolled</TH>
              <TH>Last request</TH>
              <TH className="text-right">Actions</TH>
            </TR>
          </THead>
          <TBody>
            {profiles.length === 0 ? (
              <EmptyRow colSpan={6}>No deployment profiles.</EmptyRow>
            ) : (
              profiles.map((p) => (
                <TR key={p.id}>
                  <TD className="font-medium">
                    {p.name}
                    {p.description ? <p className="text-xs font-normal text-muted-foreground">{p.description}</p> : null}
                  </TD>
                  <TD>
                    <Badge tone={p.isEnabled ? "success" : "neutral"}>{p.isEnabled ? "Enabled" : "Disabled"}</Badge>
                  </TD>
                  <TD className="text-muted-foreground">{[p.branchName, p.departmentName, p.deviceGroupName].filter(Boolean).join(" / ") || "No branch or group"}</TD>
                  <TD>
                    {p.pendingCount} / {p.enrolledCount}
                  </TD>
                  <TD className="text-muted-foreground">{formatDate(p.lastRequestAtUtc)}</TD>
                  <TD className="text-right">
                    <div className="flex flex-wrap justify-end gap-1">
                      {p.isEnabled ? (
                        <a href={installerUrl(p.id)} download className={buttonClasses({ size: "sm" })}>
                          <Download className="h-4 w-4" /> Download .exe
                        </a>
                      ) : null}
                      <Button variant="ghost" size="sm" aria-label={`Edit ${p.name}`} onClick={() => setEditing(p)}>
                        <Pencil className="h-4 w-4" />
                      </Button>
                      <Button
                        variant="ghost"
                        size="sm"
                        loading={busyId === p.id}
                        onClick={() => run(p, () => api.post(`deployment-profiles/${p.id}/${p.isEnabled ? "disable" : "enable"}`), p.isEnabled ? "Profile disabled" : "Profile enabled")}
                      >
                        {p.isEnabled ? "Disable" : "Enable"}
                      </Button>
                      <Button
                        variant="ghost"
                        size="sm"
                        aria-label={`Rotate code of ${p.name}`}
                        title="Invalidate installers downloaded before"
                        onClick={() => {
                          if (window.confirm(`Rotate the code of "${p.name}"? Installers downloaded before will no longer enroll computers.`)) {
                            void run(p, () => api.post(`deployment-profiles/${p.id}/rotate-code`), "Code rotated");
                          }
                        }}
                      >
                        <RefreshCw className="h-4 w-4" />
                      </Button>
                      <Button
                        variant="ghost"
                        size="sm"
                        aria-label={`Delete ${p.name}`}
                        onClick={() => {
                          if (window.confirm(`Delete "${p.name}" and discard its waiting requests?`)) {
                            void run(p, () => api.delete(`deployment-profiles/${p.id}`), "Profile deleted");
                          }
                        }}
                      >
                        <Trash2 className="h-4 w-4" />
                      </Button>
                    </div>
                  </TD>
                </TR>
              ))
            )}
          </TBody>
        </Table>
      </Card>
      {editing ? (
        <ProfileDialog
          profile={editing === "new" ? null : editing}
          groups={groups}
          branches={branches}
          departments={departments}
          onClose={() => setEditing(null)}
          onSaved={async () => {
            setEditing(null);
            await onChanged();
          }}
        />
      ) : null}
    </>
  );
}

function ProfileDialog({
  profile,
  groups,
  branches,
  departments,
  onClose,
  onSaved,
}: {
  profile: DeploymentProfileDto | null;
  groups: DeviceGroupDto[];
  branches: BranchDto[];
  departments: DepartmentDto[];
  onClose: () => void;
  onSaved: () => Promise<void>;
}) {
  const [name, setName] = React.useState(profile?.name ?? "");
  const [description, setDescription] = React.useState(profile?.description ?? "");
  const [branchId, setBranchId] = React.useState(profile?.branchId ?? "");
  const [departmentId, setDepartmentId] = React.useState(profile?.departmentId ?? "");
  const [groupId, setGroupId] = React.useState(profile?.deviceGroupId ?? "");
  const [errors, setErrors] = React.useState<Record<string, string>>({});
  const [error, setError] = React.useState<string | null>(null);
  const [pending, setPending] = React.useState(false);
  const branchDepartments = departments.filter((d) => d.branchId === branchId);

  async function submit(event: React.FormEvent) {
    event.preventDefault();
    setPending(true);
    setError(null);
    const body: SaveDeploymentProfileRequest = {
      name,
      description: description.trim() || null,
      branchId: branchId || null,
      departmentId: departmentId || null,
      deviceGroupId: groupId || null,
    };
    try {
      if (profile) await api.put(`deployment-profiles/${profile.id}`, body);
      else await api.post("deployment-profiles", body);
      await onSaved();
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
      title={profile ? "Edit deployment profile" : "New deployment profile"}
      footer={
        <>
          <Button variant="outline" onClick={onClose}>
            Cancel
          </Button>
          <Button type="submit" form="profile-form" loading={pending}>
            Save
          </Button>
        </>
      }
    >
      <form id="profile-form" onSubmit={submit} className="space-y-4">
        {error ? <Alert tone="error">{error}</Alert> : null}
        <Field label="Name" htmlFor="profile-name" error={errors.name} hint="e.g. Berlin store tills" required>
          <Input id="profile-name" value={name} onChange={(e) => setName(e.target.value)} required maxLength={100} autoFocus />
        </Field>
        <Field label="Description" htmlFor="profile-description" error={errors.description}>
          <Input id="profile-description" value={description} onChange={(e) => setDescription(e.target.value)} maxLength={500} />
        </Field>
        <div className="grid gap-4 sm:grid-cols-2">
          <Field label="Branch" htmlFor="profile-branch" error={errors.branchId}>
            <Select
              id="profile-branch"
              value={branchId}
              onChange={(e) => {
                setBranchId(e.target.value);
                setDepartmentId("");
              }}
            >
              <option value="">No branch</option>
              {branches
                .filter((b) => b.isActive)
                .map((b) => (
                  <option key={b.id} value={b.id}>
                    {b.name} ({b.code})
                  </option>
                ))}
            </Select>
          </Field>
          <Field label="Department" htmlFor="profile-department" error={errors.departmentId}>
            <Select id="profile-department" value={departmentId} onChange={(e) => setDepartmentId(e.target.value)} disabled={!branchId}>
              <option value="">{branchId ? "No department" : "Select a branch first"}</option>
              {branchDepartments.map((d) => (
                <option key={d.id} value={d.id}>
                  {d.name}
                </option>
              ))}
            </Select>
          </Field>
        </div>
        <Field label="Device group" htmlFor="profile-group" error={errors.deviceGroupId}>
          <Select id="profile-group" value={groupId} onChange={(e) => setGroupId(e.target.value)}>
            <option value="">No group</option>
            {groups.map((g) => (
              <option key={g.id} value={g.id}>
                {g.name}
              </option>
            ))}
          </Select>
        </Field>
      </form>
    </Dialog>
  );
}

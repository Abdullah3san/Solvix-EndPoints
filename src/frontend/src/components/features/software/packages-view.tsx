"use client";

import * as React from "react";
import { Rocket, ShieldCheck, Trash2, Upload, Wrench } from "lucide-react";
import Link from "next/link";
import { api, csrfHeader, PROXY_PREFIX } from "@/lib/api/client";
import { ApiError, problemFromResponse } from "@/lib/api/errors";
import type { ConfigurePackageRequest, DetectionKind, PackageDto, PagedResult, SilentPreset, VersionOperator } from "@/lib/api/types";
import { emptyToNull, fieldErrorsFrom, messageFrom } from "@/lib/forms";
import { formatDate } from "@/lib/utils";
import { Alert } from "@/components/ui/alert";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card } from "@/components/ui/card";
import { Dialog } from "@/components/ui/dialog";
import { Field } from "@/components/ui/field";
import { Checkbox, Input, Select, Textarea } from "@/components/ui/input";
import { Table, THead, TBody, TR, TH, TD, EmptyRow } from "@/components/ui/table";
import { useToast } from "@/components/providers/toast-provider";
import { useCan } from "@/components/providers/session-provider";
import { PackageStatusBadge, formatBytes } from "./labels";

const presets: { value: SilentPreset; label: string; exe: boolean }[] = [
  { value: "Msi", label: "MSI (msiexec /qn is applied by the agent)", exe: false },
  { value: "InnoSetup", label: "Inno Setup (/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-)", exe: true },
  { value: "Nsis", label: "NSIS (/S)", exe: true },
  { value: "WixBurn", label: "WiX Burn (/quiet /norestart)", exe: true },
  { value: "InstallShield", label: "InstallShield (/s /v/qn)", exe: true },
  { value: "Custom", label: "Custom switches", exe: true },
];

/** Uploads the file as a raw octet-stream (streamed by the BFF) with progress. */
function upload(file: File, onProgress: (fraction: number) => void): Promise<PackageDto> {
  return new Promise((resolve, reject) => {
    const xhr = new XMLHttpRequest();
    xhr.open("POST", `${PROXY_PREFIX}/software/packages?fileName=${encodeURIComponent(file.name)}`);
    xhr.setRequestHeader("Content-Type", "application/octet-stream");
    xhr.setRequestHeader("Accept", "application/json");
    for (const [k, v] of Object.entries(csrfHeader("POST"))) xhr.setRequestHeader(k, v);
    xhr.upload.onprogress = (e) => e.lengthComputable && onProgress(e.loaded / e.total);
    xhr.onerror = () => reject(new Error("The upload failed."));
    xhr.onload = async () => {
      if (xhr.status >= 200 && xhr.status < 300) return resolve(JSON.parse(xhr.responseText) as PackageDto);
      reject(new ApiError(xhr.status, await problemFromResponse(new Response(xhr.responseText, { status: xhr.status }))));
    };
    xhr.send(file);
  });
}

export function PackagesView({ initial }: { initial: PackageDto[] }) {
  const { toast } = useToast();
  const canManage = useCan("software.packages.manage");
  const canDeploy = useCan("software.deploy");
  const [packages, setPackages] = React.useState(initial);
  const [progress, setProgress] = React.useState<number | null>(null);
  const [error, setError] = React.useState<string | null>(null);
  const [configuring, setConfiguring] = React.useState<PackageDto | null>(null);
  const fileInput = React.useRef<HTMLInputElement>(null);

  async function reload() {
    setPackages((await api.get<PagedResult<PackageDto>>("software/packages", { pageSize: 100 })).items);
  }

  async function onFile(event: React.ChangeEvent<HTMLInputElement>) {
    const file = event.target.files?.[0];
    event.target.value = "";
    if (!file) return;
    setError(null);
    setProgress(0);
    try {
      const created = await upload(file, setProgress);
      toast({ tone: "success", title: "Package validated and stored", description: `SHA-256 ${created.sha256.slice(0, 16)}…` });
      await reload();
      setConfiguring(created);
    } catch (e) {
      setError(messageFrom(e));
    } finally {
      setProgress(null);
    }
  }

  async function verify(p: PackageDto) {
    try {
      const result = await api.post<{ intact: boolean }>(`software/packages/${p.id}/verify`);
      toast(result.intact ? { tone: "success", title: "Package is intact" } : { tone: "error", title: "Integrity check failed", description: "The stored file no longer matches its checksum and will not be served." });
      await reload();
    } catch (e) {
      toast({ tone: "error", title: "Could not verify", description: messageFrom(e) });
    }
  }

  async function retire(p: PackageDto) {
    if (!window.confirm(`Retire "${p.name}"? The stored file is deleted.`)) return;
    try {
      await api.post(`software/packages/${p.id}/retire`);
      toast({ tone: "success", title: "Package retired" });
      await reload();
    } catch (e) {
      toast({ tone: "error", title: "Could not retire", description: messageFrom(e) });
    }
  }

  return (
    <>
      {canManage ? (
        <div className="mb-4 flex items-center justify-end gap-3">
          {progress !== null ? <span className="text-sm text-muted-foreground">Uploading… {Math.round(progress * 100)}%</span> : null}
          <input ref={fileInput} type="file" accept=".msi,.exe" className="hidden" onChange={onFile} />
          <Button onClick={() => fileInput.current?.click()} loading={progress !== null}>
            <Upload className="h-4 w-4" /> Upload package
          </Button>
        </div>
      ) : null}
      {error ? <Alert tone="error" className="mb-4">{error}</Alert> : null}
      <Card>
        <Table>
          <THead>
            <TR>
              <TH>Package</TH>
              <TH>Type</TH>
              <TH>Signature</TH>
              <TH>Size</TH>
              <TH>Status</TH>
              <TH className="text-right">Actions</TH>
            </TR>
          </THead>
          <TBody>
            {packages.length === 0 ? (
              <EmptyRow colSpan={6}>No packages yet. Upload an .msi or .exe installer.</EmptyRow>
            ) : (
              packages.map((p) => (
                <TR key={p.id}>
                  <TD>
                    <div className="font-medium">{p.name}{p.version ? <span className="text-muted-foreground"> {p.version}</span> : null}</div>
                    <div className="font-mono text-xs text-muted-foreground" title={p.sha256}>{p.sha256.slice(0, 16)}… · {p.originalFileName}</div>
                    {p.blockedByRule ? <Badge tone="danger">Blocked by “{p.blockedByRule}”</Badge> : !p.isConfigured ? <Badge tone="warning">Not configured</Badge> : null}
                  </TD>
                  <TD>{p.installerType}{p.architecture ? ` · ${p.architecture}` : ""}</TD>
                  <TD>
                    {p.signatureState === "Signed" ? (
                      <span title={p.signerSubject ?? undefined}>
                        <Badge tone={p.signerTrusted ? "success" : "warning"}>{p.signerTrusted ? "Trusted publisher" : "Signed, untrusted"}</Badge>
                      </span>
                    ) : (
                      <Badge tone="warning">Unsigned</Badge>
                    )}
                  </TD>
                  <TD>{formatBytes(p.sizeBytes)}</TD>
                  <TD>
                    <PackageStatusBadge status={p.status} />
                    <div className="text-xs text-muted-foreground">verified {formatDate(p.lastVerifiedAtUtc)}</div>
                  </TD>
                  <TD>
                    <div className="flex justify-end gap-1">
                      {canDeploy && p.status === "Ready" && p.isConfigured ? (
                        <Link href={`/software/deployments/new?packageId=${p.id}`} aria-label={`Deploy ${p.name}`}>
                          <Button variant="ghost" size="icon" tabIndex={-1}><Rocket className="h-4 w-4" /></Button>
                        </Link>
                      ) : null}
                      {canManage && p.status !== "Retired" ? (
                        <>
                          <Button variant="ghost" size="icon" onClick={() => setConfiguring(p)} aria-label={`Configure ${p.name}`}><Wrench className="h-4 w-4" /></Button>
                          <Button variant="ghost" size="icon" onClick={() => verify(p)} aria-label={`Verify ${p.name}`}><ShieldCheck className="h-4 w-4" /></Button>
                          <Button variant="ghost" size="icon" onClick={() => retire(p)} aria-label={`Retire ${p.name}`} className="text-destructive"><Trash2 className="h-4 w-4" /></Button>
                        </>
                      ) : null}
                    </div>
                  </TD>
                </TR>
              ))
            )}
          </TBody>
        </Table>
      </Card>
      {configuring ? (
        <ConfigureDialog
          pkg={configuring}
          onClose={() => setConfiguring(null)}
          onSaved={async () => {
            setConfiguring(null);
            toast({ tone: "success", title: "Package configured" });
            await reload();
          }}
        />
      ) : null}
    </>
  );
}

const lines = (value: string) => value.split("\n").map((l) => l.trim()).filter(Boolean);

function ConfigureDialog({ pkg, onClose, onSaved }: { pkg: PackageDto; onClose: () => void; onSaved: () => Promise<void> }) {
  const isMsi = pkg.installerType === "Msi";
  const [name, setName] = React.useState(pkg.name);
  const [publisher, setPublisher] = React.useState(pkg.publisher ?? "");
  const [version, setVersion] = React.useState(pkg.version ?? "");
  const [description, setDescription] = React.useState(pkg.description ?? "");
  const [sensitive, setSensitive] = React.useState(pkg.isSensitive);
  const [preset, setPreset] = React.useState<SilentPreset>(isMsi ? "Msi" : pkg.isConfigured ? pkg.silentPreset : "InnoSetup");
  const [installArgs, setInstallArgs] = React.useState(pkg.installArguments.join("\n"));
  const [uninstallArgs, setUninstallArgs] = React.useState(pkg.uninstallArguments.join("\n"));
  const [usePresetArgs, setUsePresetArgs] = React.useState(!pkg.isConfigured || pkg.silentPreset !== "Custom");
  const [timeout, setTimeoutMinutes] = React.useState(pkg.timeoutMinutes);
  const [success, setSuccess] = React.useState(pkg.successExitCodes.join(","));
  const [reboot, setReboot] = React.useState(pkg.rebootExitCodes.join(","));
  const [kind, setKind] = React.useState<DetectionKind>(pkg.detection?.kind ?? (isMsi ? "MsiProductCode" : "UninstallEntry"));
  const [productCode, setProductCode] = React.useState(pkg.detection?.productCode ?? "");
  const [displayName, setDisplayName] = React.useState(pkg.detection?.displayName ?? pkg.name);
  const [detPublisher, setDetPublisher] = React.useState(pkg.detection?.publisher ?? pkg.publisher ?? "");
  const [versionOp, setVersionOp] = React.useState<VersionOperator>(pkg.detection?.versionOperator ?? "AtLeast");
  const [detVersion, setDetVersion] = React.useState(pkg.detection?.version ?? pkg.version ?? "");
  const [errors, setErrors] = React.useState<Record<string, string>>({});
  const [error, setError] = React.useState<string | null>(null);
  const [pending, setPending] = React.useState(false);

  const codes = (v: string) => v.split(",").map((c) => c.trim()).filter(Boolean).map(Number);

  async function submit(event: React.FormEvent) {
    event.preventDefault();
    setPending(true);
    setError(null);
    setErrors({});
    const custom = !isMsi && (preset === "Custom" || !usePresetArgs);
    const body: ConfigurePackageRequest = {
      name,
      publisher: emptyToNull(publisher),
      version: emptyToNull(version),
      description: emptyToNull(description),
      isSensitive: sensitive,
      silentPreset: preset,
      installArguments: isMsi ? lines(installArgs) : custom ? lines(installArgs) : null,
      uninstallArguments: isMsi ? null : custom ? lines(uninstallArgs) : null,
      successExitCodes: codes(success),
      rebootExitCodes: codes(reboot),
      timeoutMinutes: timeout,
      detection: {
        kind,
        productCode: kind === "MsiProductCode" ? productCode.trim() : null,
        displayName: kind === "UninstallEntry" ? displayName.trim() : null,
        nameMatch: "Exact",
        publisher: kind === "UninstallEntry" ? emptyToNull(detPublisher) : null,
        version: versionOp === "Any" ? null : detVersion.trim(),
        versionOperator: versionOp,
      },
    };
    try {
      await api.put(`software/packages/${pkg.id}`, body);
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
      title={`Configure ${pkg.originalFileName}`}
      description="How the package is installed silently, removed and recognised on a device."
      footer={
        <>
          <Button variant="outline" onClick={onClose}>Cancel</Button>
          <Button type="submit" form="package-form" loading={pending}>Save</Button>
        </>
      }
    >
      <form id="package-form" onSubmit={submit} className="space-y-4">
        {error ? <Alert tone="error">{error}</Alert> : null}
        <div className="grid gap-4 sm:grid-cols-2">
          <Field label="Name" htmlFor="pkg-name" error={errors.name} required><Input id="pkg-name" value={name} onChange={(e) => setName(e.target.value)} required maxLength={200} /></Field>
          <Field label="Version" htmlFor="pkg-version" error={errors.version}><Input id="pkg-version" value={version} onChange={(e) => setVersion(e.target.value)} maxLength={64} /></Field>
          <Field label="Publisher" htmlFor="pkg-publisher" error={errors.publisher}><Input id="pkg-publisher" value={publisher} onChange={(e) => setPublisher(e.target.value)} maxLength={200} /></Field>
          <Field label="Timeout (minutes)" htmlFor="pkg-timeout" error={errors.timeoutMinutes}><Input id="pkg-timeout" type="number" min={1} max={240} value={timeout} onChange={(e) => setTimeoutMinutes(Number(e.target.value))} /></Field>
        </div>
        <Field label="Description" htmlFor="pkg-description"><Textarea id="pkg-description" value={description} onChange={(e) => setDescription(e.target.value)} maxLength={2000} /></Field>
        <label className="flex items-center gap-2 text-sm"><Checkbox checked={sensitive} onChange={(e) => setSensitive(e.target.checked)} /> Sensitive: every deployment needs the additional approval</label>

        <fieldset className="space-y-3 rounded-md border p-3">
          <legend className="px-1 text-sm font-medium">Silent installation</legend>
          {isMsi ? (
            <Field label="Public MSI properties (one per line, NAME=value)" htmlFor="pkg-args" error={errors.installArguments} hint="msiexec /qn /norestart and logging are set by the agent.">
              <Textarea id="pkg-args" value={installArgs} onChange={(e) => setInstallArgs(e.target.value)} placeholder="ALLUSERS=1" className="font-mono" />
            </Field>
          ) : (
            <>
              <Field label="Installer framework" htmlFor="pkg-preset">
                <Select id="pkg-preset" value={preset} onChange={(e) => setPreset(e.target.value as SilentPreset)}>
                  {presets.filter((p) => p.exe).map((p) => <option key={p.value} value={p.value}>{p.label}</option>)}
                </Select>
              </Field>
              {preset !== "Custom" ? <label className="flex items-center gap-2 text-sm"><Checkbox checked={usePresetArgs} onChange={(e) => setUsePresetArgs(e.target.checked)} /> Use the preset’s switches</label> : null}
              {preset === "Custom" || !usePresetArgs ? (
                <div className="grid gap-4 sm:grid-cols-2">
                  <Field label="Install switches (one per line)" htmlFor="pkg-args" error={errors.installArguments}><Textarea id="pkg-args" value={installArgs} onChange={(e) => setInstallArgs(e.target.value)} className="font-mono" /></Field>
                  <Field label="Uninstall switches (one per line)" htmlFor="pkg-uargs" error={errors.uninstallArguments} hint="Leave empty if the package cannot be uninstalled."><Textarea id="pkg-uargs" value={uninstallArgs} onChange={(e) => setUninstallArgs(e.target.value)} className="font-mono" /></Field>
                </div>
              ) : null}
            </>
          )}
          <div className="grid gap-4 sm:grid-cols-2">
            <Field label="Success exit codes" htmlFor="pkg-success" hint="Comma separated"><Input id="pkg-success" value={success} onChange={(e) => setSuccess(e.target.value)} /></Field>
            <Field label="Reboot-required exit codes" htmlFor="pkg-reboot" hint="Counted as success"><Input id="pkg-reboot" value={reboot} onChange={(e) => setReboot(e.target.value)} /></Field>
          </div>
        </fieldset>

        <fieldset className="space-y-3 rounded-md border p-3">
          <legend className="px-1 text-sm font-medium">Application version detection</legend>
          <Field label="Detect by" htmlFor="det-kind" error={errors.detection}>
            <Select id="det-kind" value={kind} onChange={(e) => setKind(e.target.value as DetectionKind)}>
              <option value="MsiProductCode">MSI product code</option>
              <option value="UninstallEntry">Installed programs entry (name)</option>
            </Select>
          </Field>
          {kind === "MsiProductCode" ? (
            <Field label="Product code" htmlFor="det-code"><Input id="det-code" value={productCode} onChange={(e) => setProductCode(e.target.value)} placeholder="{12345678-1234-1234-1234-123456789ABC}" className="font-mono" /></Field>
          ) : (
            <div className="grid gap-4 sm:grid-cols-2">
              <Field label="Display name" htmlFor="det-name"><Input id="det-name" value={displayName} onChange={(e) => setDisplayName(e.target.value)} /></Field>
              <Field label="Publisher (optional)" htmlFor="det-pub"><Input id="det-pub" value={detPublisher} onChange={(e) => setDetPublisher(e.target.value)} /></Field>
            </div>
          )}
          <div className="grid gap-4 sm:grid-cols-2">
            <Field label="Version condition" htmlFor="det-op">
              <Select id="det-op" value={versionOp} onChange={(e) => setVersionOp(e.target.value as VersionOperator)}>
                <option value="Any">Any version</option>
                <option value="AtLeast">This version or newer</option>
                <option value="Equal">Exactly this version</option>
              </Select>
            </Field>
            {versionOp !== "Any" ? <Field label="Version" htmlFor="det-ver"><Input id="det-ver" value={detVersion} onChange={(e) => setDetVersion(e.target.value)} placeholder="5.2.0" /></Field> : null}
          </div>
        </fieldset>
      </form>
    </Dialog>
  );
}

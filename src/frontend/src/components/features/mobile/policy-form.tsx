"use client";

import * as React from "react";
import { Plus, Trash2 } from "lucide-react";
import type {
  AppControlMode,
  PasswordQuality,
  Scenario,
  MobilePolicySettings,
  VpnSettings,
  WifiNetworkSettings,
  WifiSecurity,
} from "@/lib/api/mdm-types";
import { intOrNull } from "@/lib/mdm";
import { Button } from "@/components/ui/button";
import { Field } from "@/components/ui/field";
import { Checkbox, Input, Select, Textarea } from "@/components/ui/input";

const qualityLabel: Record<PasswordQuality, string> = {
  Required: "Any screen lock",
  Numeric: "Numeric PIN",
  NumericComplex: "Numeric, no repeats/sequences",
  Alphabetic: "Letters",
  Alphanumeric: "Letters and numbers",
  Complex: "Letters, numbers and symbols",
};

const securityLabel: Record<WifiSecurity, string> = { Open: "Open", WpaPersonal: "WPA2/3 personal (password)", WpaEnterprisePeap: "WPA2/3 enterprise (PEAP)" };

function Section({ title, hint, children }: { title: string; hint?: string; children: React.ReactNode }) {
  return (
    <fieldset className="space-y-3 rounded-md border border-border p-4">
      <legend className="px-1 text-sm font-semibold">{title}</legend>
      {hint ? <p className="text-xs text-muted-foreground">{hint}</p> : null}
      {children}
    </fieldset>
  );
}

function Check({ label, checked, onChange, hint }: { label: string; checked: boolean; onChange: (v: boolean) => void; hint?: string }) {
  return (
    <label className="flex items-start gap-2 text-sm">
      <Checkbox className="mt-0.5" checked={checked} onChange={(e) => onChange(e.target.checked)} />
      <span>
        {label}
        {hint ? <span className="block text-xs text-muted-foreground">{hint}</span> : null}
      </span>
    </label>
  );
}

const num = (v: number | null) => (v === null ? "" : String(v));

export function PolicySettingsForm({
  scenario,
  value,
  onChange,
  errors,
}: {
  scenario: Scenario;
  value: MobilePolicySettings;
  onChange: (next: MobilePolicySettings) => void;
  errors: Record<string, string>;
}) {
  const android = scenario.platform === "Android";
  const personal = scenario.ownership === "PersonallyOwned";
  const userEnrollment = scenario.mode === "UserEnrollment";
  const set = (patch: Partial<MobilePolicySettings>) => onChange({ ...value, ...patch });
  const pw = value.password;

  const updateWifi = (i: number, patch: Partial<WifiNetworkSettings>) =>
    set({ wifiNetworks: value.wifiNetworks.map((w, idx) => (idx === i ? { ...w, ...patch } : w)) });
  const updateVpn = (i: number, patch: Partial<VpnSettings>) => set({ vpn: value.vpn.map((v, idx) => (idx === i ? { ...v, ...patch } : v)) });

  return (
    <div className="space-y-5">
      {personal ? (
        <p className="rounded-md bg-muted p-3 text-sm">
          Personal devices: only work data is managed. Sharing from work apps to personal apps is always blocked, and settings that would reach the
          personal side are skipped. Publishing shows exactly what was skipped.
        </p>
      ) : null}

      <Section title="Passcode">
        <Check
          label="Enforce a passcode policy"
          checked={pw !== null}
          onChange={(on) =>
            set({ password: on ? { quality: "Required", minLength: 6, maxFailedAttempts: null, maxInactivitySeconds: null, expirationDays: null, historyLength: null } : null })
          }
          hint={scenario.mode === "WorkProfile" ? "Applies to the work profile challenge." : undefined}
        />
        {pw ? (
          <div className="grid gap-3 sm:grid-cols-2">
            <Field label="Complexity" htmlFor="pw-quality">
              <Select id="pw-quality" value={pw.quality} onChange={(e) => set({ password: { ...pw, quality: e.target.value as PasswordQuality } })}>
                {(Object.keys(qualityLabel) as PasswordQuality[]).map((q) => (
                  <option key={q} value={q}>{qualityLabel[q]}</option>
                ))}
              </Select>
            </Field>
            <Field label="Minimum length" htmlFor="pw-min" error={errors["settings.password.minLength"]}>
              <Input id="pw-min" inputMode="numeric" value={String(pw.minLength)} onChange={(e) => set({ password: { ...pw, minLength: intOrNull(e.target.value) ?? 0 } })} />
            </Field>
            <Field label="Erase after failed attempts" htmlFor="pw-fail" error={errors["settings.password.maxFailedAttempts"]} hint={personal ? "Removes work data only" : userEnrollment ? "Not available on User Enrollment" : "Optional"}>
              <Input id="pw-fail" inputMode="numeric" value={num(pw.maxFailedAttempts)} onChange={(e) => set({ password: { ...pw, maxFailedAttempts: intOrNull(e.target.value) } })} />
            </Field>
            <Field label="Auto-lock after (seconds)" htmlFor="pw-idle" error={errors["settings.password.maxInactivitySeconds"]}>
              <Input id="pw-idle" inputMode="numeric" value={num(pw.maxInactivitySeconds)} onChange={(e) => set({ password: { ...pw, maxInactivitySeconds: intOrNull(e.target.value) } })} />
            </Field>
            <Field label="Expires after (days)" htmlFor="pw-exp" error={errors["settings.password.expirationDays"]}>
              <Input id="pw-exp" inputMode="numeric" value={num(pw.expirationDays)} onChange={(e) => set({ password: { ...pw, expirationDays: intOrNull(e.target.value) } })} />
            </Field>
            <Field label="Remember previous passcodes" htmlFor="pw-hist" error={errors["settings.password.historyLength"]}>
              <Input id="pw-hist" inputMode="numeric" value={num(pw.historyLength)} onChange={(e) => set({ password: { ...pw, historyLength: intOrNull(e.target.value) } })} />
            </Field>
          </div>
        ) : null}
      </Section>

      <Section title="Restrictions" hint={userEnrollment ? "User Enrollment cannot restrict the device itself; device-wide options are skipped." : undefined}>
        <div className="grid gap-2 sm:grid-cols-2">
          <Check label="Disable camera" checked={value.restrictions.disableCamera} onChange={(v) => set({ restrictions: { ...value.restrictions, disableCamera: v } })} />
          <Check label="Disable screen capture" checked={value.restrictions.disableScreenCapture} onChange={(v) => set({ restrictions: { ...value.restrictions, disableScreenCapture: v } })} />
          {android ? <Check label="Disable USB file transfer" checked={value.restrictions.disableUsbFileTransfer} onChange={(v) => set({ restrictions: { ...value.restrictions, disableUsbFileTransfer: v } })} /> : null}
          {android ? <Check label="Block apps from unknown sources" checked={value.restrictions.disableUnknownSources} onChange={(v) => set({ restrictions: { ...value.restrictions, disableUnknownSources: v } })} /> : null}
          <Check label="Prevent factory reset / erase" checked={value.restrictions.disableFactoryReset} onChange={(v) => set({ restrictions: { ...value.restrictions, disableFactoryReset: v } })} hint={personal ? "Not applied to personal devices" : !android ? "Supervised devices only" : undefined} />
          <Check
            label="Block sharing from work to personal apps"
            checked={personal || value.restrictions.blockManagedToPersonalSharing}
            onChange={(v) => set({ restrictions: { ...value.restrictions, blockManagedToPersonalSharing: v } })}
            hint={personal ? "Always on for personal devices" : undefined}
          />
        </div>
      </Section>

      <Section title="Compliance requirements" hint="Devices that fail a requirement are shown as non-compliant.">
        <div className="grid gap-3 sm:grid-cols-2">
          <Field label="Minimum OS version" htmlFor="c-os" error={errors["settings.compliance.minimumOsVersion"]} hint="e.g. 14 or 17.4">
            <Input id="c-os" value={value.compliance.minimumOsVersion ?? ""} onChange={(e) => set({ compliance: { ...value.compliance, minimumOsVersion: e.target.value.trim() || null } })} />
          </Field>
          <Field label="Max hours since last contact" htmlFor="c-hours" error={errors["settings.compliance.maxHoursSinceCheckIn"]}>
            <Input id="c-hours" inputMode="numeric" value={num(value.compliance.maxHoursSinceCheckIn)} onChange={(e) => set({ compliance: { ...value.compliance, maxHoursSinceCheckIn: intOrNull(e.target.value) } })} />
          </Field>
        </div>
        <div className="grid gap-2 sm:grid-cols-2">
          <Check label="Require a device passcode" checked={value.compliance.requirePasscode} onChange={(v) => set({ compliance: { ...value.compliance, requirePasscode: v } })} />
          <Check label="Require encryption" checked={value.compliance.requireEncryption} onChange={(v) => set({ compliance: { ...value.compliance, requireEncryption: v } })} />
          <Check label="Block rooted / compromised devices" checked={value.compliance.blockCompromisedDevices} onChange={(v) => set({ compliance: { ...value.compliance, blockCompromisedDevices: v } })} hint={!android ? "iOS reports no integrity signal; this rule is not evaluated" : undefined} />
          <Check label="Require the latest policy to be applied" checked={value.compliance.requireAssignedPolicyApplied} onChange={(v) => set({ compliance: { ...value.compliance, requireAssignedPolicyApplied: v } })} />
        </div>
      </Section>

      <Section title="Wi-Fi">
        {value.wifiNetworks.map((w, i) => (
          <div key={i} className="space-y-3 rounded-md bg-muted/40 p-3">
            <div className="grid gap-3 sm:grid-cols-2">
              <Field label="Network name (SSID)" htmlFor={`w-ssid-${i}`} error={errors[`settings.wifiNetworks[${i}].ssid`]}>
                <Input id={`w-ssid-${i}`} value={w.ssid} maxLength={32} onChange={(e) => updateWifi(i, { ssid: e.target.value })} />
              </Field>
              <Field label="Security" htmlFor={`w-sec-${i}`}>
                <Select id={`w-sec-${i}`} value={w.security} onChange={(e) => updateWifi(i, { security: e.target.value as WifiSecurity })}>
                  {(Object.keys(securityLabel) as WifiSecurity[]).map((s) => (
                    <option key={s} value={s}>{securityLabel[s]}</option>
                  ))}
                </Select>
              </Field>
              {w.security === "WpaPersonal" ? (
                <Field label="Password" htmlFor={`w-pw-${i}`} error={errors[`settings.wifiNetworks[${i}].passphrase`]} hint={w.passphraseSet ? "A password is stored. Leave blank to keep it." : "8–63 characters"}>
                  <Input id={`w-pw-${i}`} type="password" autoComplete="new-password" value={w.passphrase ?? ""} onChange={(e) => updateWifi(i, { passphrase: e.target.value || null })} />
                </Field>
              ) : null}
            </div>
            {w.security === "WpaEnterprisePeap" ? (
              <div className="grid gap-3 sm:grid-cols-2">
                <Field className="sm:col-span-2" label="Server CA certificate (PEM)" htmlFor={`w-ca-${i}`} error={errors[`settings.wifiNetworks[${i}].serverCaCertificatePem`]}>
                  <Textarea id={`w-ca-${i}`} className="font-mono text-xs" value={w.serverCaCertificatePem ?? ""} onChange={(e) => updateWifi(i, { serverCaCertificatePem: e.target.value || null })} />
                </Field>
                <Field label="Trusted server names" htmlFor={`w-names-${i}`} hint="Comma separated">
                  <Input id={`w-names-${i}`} value={w.serverNames.join(", ")} onChange={(e) => updateWifi(i, { serverNames: e.target.value.split(",").map((s) => s.trim()).filter(Boolean) })} />
                </Field>
                <Field label="Anonymous identity" htmlFor={`w-anon-${i}`}>
                  <Input id={`w-anon-${i}`} value={w.anonymousIdentity ?? ""} onChange={(e) => updateWifi(i, { anonymousIdentity: e.target.value || null })} />
                </Field>
              </div>
            ) : null}
            <div className="flex items-center justify-between">
              <div className="flex gap-4">
                <Check label="Join automatically" checked={w.autoJoin} onChange={(v) => updateWifi(i, { autoJoin: v })} />
                <Check label="Hidden network" checked={w.hidden} onChange={(v) => updateWifi(i, { hidden: v })} />
              </div>
              <Button variant="ghost" size="sm" className="text-destructive" onClick={() => set({ wifiNetworks: value.wifiNetworks.filter((_, idx) => idx !== i) })}>
                <Trash2 className="h-4 w-4" /> Remove
              </Button>
            </div>
          </div>
        ))}
        <Button
          variant="outline"
          size="sm"
          onClick={() =>
            set({ wifiNetworks: [...value.wifiNetworks, { ssid: "", security: "WpaPersonal", passphrase: null, passphraseSet: false, autoJoin: true, hidden: false, serverCaCertificatePem: null, serverNames: [], anonymousIdentity: null }] })
          }
        >
          <Plus className="h-4 w-4" /> Add network
        </Button>
      </Section>

      <Section title="VPN" hint={android ? "Android uses one always-on VPN app, which is installed automatically." : userEnrollment ? "User Enrollment only supports per-app VPN, which is not available yet; VPNs here are skipped." : "iOS uses the built-in IKEv2 client."}>
        {value.vpn.map((v, i) => (
          <div key={i} className="space-y-3 rounded-md bg-muted/40 p-3">
            <div className="grid gap-3 sm:grid-cols-2">
              <Field label="Name" htmlFor={`v-name-${i}`} error={errors[`settings.vpn[${i}].name`]}>
                <Input id={`v-name-${i}`} value={v.name} onChange={(e) => updateVpn(i, { name: e.target.value })} />
              </Field>
              {android ? (
                <Field label="VPN app package name" htmlFor={`v-pkg-${i}`} error={errors[`settings.vpn[${i}].packageName`]}>
                  <Input id={`v-pkg-${i}`} value={v.packageName ?? ""} onChange={(e) => updateVpn(i, { packageName: e.target.value || null })} />
                </Field>
              ) : (
                <>
                  <Field label="Gateway" htmlFor={`v-srv-${i}`} error={errors[`settings.vpn[${i}].server`]}>
                    <Input id={`v-srv-${i}`} value={v.server ?? ""} onChange={(e) => updateVpn(i, { server: e.target.value || null })} />
                  </Field>
                  <Field label="Remote identifier" htmlFor={`v-rid-${i}`}>
                    <Input id={`v-rid-${i}`} value={v.remoteIdentifier ?? ""} onChange={(e) => updateVpn(i, { remoteIdentifier: e.target.value || null })} />
                  </Field>
                  <Field label="Shared secret" htmlFor={`v-sec-${i}`} error={errors[`settings.vpn[${i}].sharedSecret`]} hint={v.sharedSecretSet ? "A secret is stored. Leave blank to keep it." : undefined}>
                    <Input id={`v-sec-${i}`} type="password" autoComplete="new-password" value={v.sharedSecret ?? ""} onChange={(e) => updateVpn(i, { sharedSecret: e.target.value || null })} />
                  </Field>
                </>
              )}
            </div>
            <div className="flex items-center justify-between">
              {android ? <Check label="Block all traffic while the VPN is down" checked={v.lockdown} onChange={(on) => updateVpn(i, { lockdown: on })} /> : <span />}
              <Button variant="ghost" size="sm" className="text-destructive" onClick={() => set({ vpn: value.vpn.filter((_, idx) => idx !== i) })}>
                <Trash2 className="h-4 w-4" /> Remove
              </Button>
            </div>
          </div>
        ))}
        {errors["settings.vpn"] ? <p className="text-xs text-destructive" role="alert">{errors["settings.vpn"]}</p> : null}
        <Button
          variant="outline"
          size="sm"
          disabled={android && value.vpn.length >= 1}
          onClick={() =>
            set({
              vpn: [
                ...value.vpn,
                { name: "", type: android ? "AlwaysOnApp" : "IkeV2", packageName: null, lockdown: false, server: null, remoteIdentifier: null, localIdentifier: null, sharedSecret: null, sharedSecretSet: false },
              ],
            })
          }
        >
          <Plus className="h-4 w-4" /> Add VPN
        </Button>
      </Section>

      {scenario.mode === "Dedicated" ? (
        <Section title="Kiosk" hint="The device is locked to these apps. Mark the main app with the Kiosk install type under Apps.">
          <Field label="Allowed packages" htmlFor="kiosk-pkgs" error={errors["settings.kiosk.allowedPackages"] ?? errors["settings.kiosk"]} hint="One package name per line">
            <Textarea
              id="kiosk-pkgs"
              className="font-mono text-xs"
              value={(value.kiosk?.allowedPackages ?? []).join("\n")}
              onChange={(e) => {
                const pkgs = e.target.value.split("\n").map((s) => s.trim()).filter(Boolean);
                set({ kiosk: pkgs.length === 0 && !e.target.value ? null : { disableStatusBar: true, disableKeyguard: true, keepScreenOn: true, ...value.kiosk, allowedPackages: pkgs } });
              }}
            />
          </Field>
        </Section>
      ) : null}

      <Section title="Application control">
        <Field label="Mode" htmlFor="app-control">
          <Select id="app-control" value={value.appControl} onChange={(e) => set({ appControl: e.target.value as AppControlMode })}>
            <option value="BlockListOnly">Allow everything except blocked apps</option>
            <option value="ApprovedAppsOnly">Only apps assigned to this policy</option>
          </Select>
        </Field>
        {!android && value.appControl === "ApprovedAppsOnly" ? <p className="text-xs text-muted-foreground">On iOS this needs supervised devices (Automated Device Enrollment).</p> : null}
      </Section>
    </div>
  );
}

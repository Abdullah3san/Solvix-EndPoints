"use client";

import * as React from "react";
import { useRouter } from "next/navigation";
import QRCode from "qrcode";
import { Copy, Laptop, ShieldCheck, ShieldOff } from "lucide-react";
import { api } from "@/lib/api/client";
import type { MfaSetupDto, MfaStatusDto, PasswordRulesDto, SessionDto } from "@/lib/api/types";
import { fieldErrorsFrom, messageFrom } from "@/lib/forms";
import { formatDate } from "@/lib/utils";
import { Button } from "@/components/ui/button";
import { Badge } from "@/components/ui/badge";
import { Dialog } from "@/components/ui/dialog";
import { Field } from "@/components/ui/field";
import { Input } from "@/components/ui/input";
import { Alert } from "@/components/ui/alert";
import { Card, CardContent, CardDescription, CardFooter, CardHeader, CardTitle } from "@/components/ui/card";
import { useToast } from "@/components/providers/toast-provider";
import { PasswordRulesHint } from "./auth-forms";

export function AccountSecurity({ mfa, sessions, rules }: { mfa: MfaStatusDto; sessions: SessionDto[]; rules: PasswordRulesDto }) {
  return (
    <div className="space-y-6">
      <MfaCard initial={mfa} />
      <PasswordCard rules={rules} />
      <SessionsCard initial={sessions} />
    </div>
  );
}

function PasswordCard({ rules }: { rules: PasswordRulesDto }) {
  const { toast } = useToast();
  const [errors, setErrors] = React.useState<Record<string, string>>({});
  const [pending, setPending] = React.useState(false);

  async function submit(event: React.FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const form = event.currentTarget;
    const data = new FormData(form);
    const newPassword = String(data.get("newPassword") ?? "");
    if (newPassword !== String(data.get("confirm") ?? "")) {
      setErrors({ confirm: "The passwords do not match." });
      return;
    }
    setErrors({});
    setPending(true);
    try {
      await api.post("auth/change-password", { currentPassword: String(data.get("currentPassword") ?? ""), newPassword });
      form.reset();
      toast({ tone: "success", title: "Password changed", description: "Your other sessions were signed out." });
    } catch (e) {
      const fields = fieldErrorsFrom(e);
      setErrors(Object.keys(fields).length ? fields : { newPassword: messageFrom(e) });
    } finally {
      setPending(false);
    }
  }

  return (
    <form onSubmit={submit}>
      <Card>
        <CardHeader>
          <CardTitle>Password</CardTitle>
          <CardDescription>
            <PasswordRulesHint rules={rules} /> Your last {rules.historyCount} passwords cannot be reused.
          </CardDescription>
        </CardHeader>
        <CardContent className="grid gap-4 sm:grid-cols-3">
          <Field label="Current password" htmlFor="currentPassword" error={errors.currentPassword} required>
            <Input id="currentPassword" name="currentPassword" type="password" autoComplete="current-password" required />
          </Field>
          <Field label="New password" htmlFor="newPassword" error={errors.newPassword} required>
            <Input id="newPassword" name="newPassword" type="password" autoComplete="new-password" required />
          </Field>
          <Field label="Repeat new password" htmlFor="confirm" error={errors.confirm} required>
            <Input id="confirm" name="confirm" type="password" autoComplete="new-password" required />
          </Field>
        </CardContent>
        <CardFooter className="justify-end">
          <Button type="submit" loading={pending}>
            Change password
          </Button>
        </CardFooter>
      </Card>
    </form>
  );
}

function MfaCard({ initial }: { initial: MfaStatusDto }) {
  const router = useRouter();
  const { toast } = useToast();
  const [status, setStatus] = React.useState(initial);
  const [setup, setSetup] = React.useState<(MfaSetupDto & { qr: string }) | null>(null);
  const [codes, setCodes] = React.useState<string[] | null>(null);
  const [dialog, setDialog] = React.useState<"start" | "disable" | "regenerate" | null>(null);
  const [errors, setErrors] = React.useState<Record<string, string>>({});
  const [pending, setPending] = React.useState(false);

  async function refresh() {
    setStatus(await api.get<MfaStatusDto>("auth/mfa"));
    router.refresh();
  }

  async function run(event: React.FormEvent<HTMLFormElement>, action: () => Promise<void>) {
    event.preventDefault();
    setErrors({});
    setPending(true);
    try {
      await action();
    } catch (e) {
      const fields = fieldErrorsFrom(e);
      setErrors(Object.keys(fields).length ? fields : { code: messageFrom(e) });
    } finally {
      setPending(false);
    }
  }

  const value = (form: HTMLFormElement, name: string) => String(new FormData(form).get(name) ?? "");

  return (
    <Card>
      <CardHeader className="flex-row items-start justify-between gap-3">
        <div>
          <CardTitle className="flex items-center gap-2">
            Two-step verification
            {status.enabled ? <Badge tone="success">On</Badge> : <Badge tone="warning">Off</Badge>}
          </CardTitle>
          <CardDescription>
            {status.enabled
              ? `Signing in asks for a code from your authenticator app. ${status.recoveryCodesRemaining} recovery codes left.`
              : "Protect your account with a code from an authenticator app (Google Authenticator, Microsoft Authenticator, 1Password…)."}
          </CardDescription>
        </div>
        {status.enabled ? <ShieldCheck className="h-6 w-6 text-success" /> : <ShieldOff className="h-6 w-6 text-muted-foreground" />}
      </CardHeader>
      <CardContent className="space-y-4">
        {status.requiredByWorkspace && !status.enabled ? <Alert tone="error">This workspace requires two-step verification.</Alert> : null}
        {codes ? (
          <Alert tone="success" title="Save your recovery codes">
            Each code signs you in once if you lose your phone. They are shown only now.
            <ul className="mt-2 grid grid-cols-2 gap-1 font-mono text-sm text-foreground">
              {codes.map((c) => (
                <li key={c}>{c}</li>
              ))}
            </ul>
            <Button
              variant="outline"
              size="sm"
              className="mt-2"
              onClick={() => navigator.clipboard?.writeText(codes.join("\n")).then(() => toast({ tone: "success", title: "Copied" }))}
            >
              <Copy className="h-3 w-3" /> Copy
            </Button>
          </Alert>
        ) : null}
        {setup ? (
          <form
            className="grid gap-4 sm:grid-cols-[auto_1fr]"
            onSubmit={(e) =>
              run(e, async () => {
                const result = await api.post<{ recoveryCodes: string[] }>("auth/mfa/totp/confirm", { code: value(e.currentTarget, "code") });
                setSetup(null);
                setCodes(result.recoveryCodes);
                toast({ tone: "success", title: "Two-step verification is on" });
                await refresh();
              })
            }
          >
            {/* eslint-disable-next-line @next/next/no-img-element -- generated data URL */}
            <img src={setup.qr} alt="QR code for your authenticator app" width={176} height={176} className="rounded-md border border-border bg-white p-2" />
            <div className="space-y-3">
              <p className="text-sm">1. Scan the code with your authenticator app, or enter this key:</p>
              <code className="block break-all rounded bg-muted px-2 py-1 text-sm">{setup.secret.replace(/(.{4})/g, "$1 ").trim()}</code>
              <Field label="2. Enter the 6-digit code it shows" htmlFor="code" error={errors.code} required>
                <Input id="code" name="code" inputMode="numeric" autoComplete="one-time-code" maxLength={7} className="w-40" required autoFocus />
              </Field>
              <div className="flex gap-2">
                <Button type="submit" loading={pending}>
                  Turn on
                </Button>
                <Button variant="outline" onClick={() => setSetup(null)}>
                  Cancel
                </Button>
              </div>
            </div>
          </form>
        ) : null}
      </CardContent>
      {!setup ? (
        <CardFooter className="justify-end gap-2">
          {status.enabled ? (
            <>
              <Button variant="outline" onClick={() => setDialog("regenerate")}>
                New recovery codes
              </Button>
              <Button variant="destructive" onClick={() => setDialog("disable")}>
                Turn off
              </Button>
            </>
          ) : (
            <Button onClick={() => setDialog("start")}>Set up authenticator app</Button>
          )}
        </CardFooter>
      ) : null}

      <Dialog
        open={dialog !== null}
        onClose={() => {
          setDialog(null);
          setErrors({});
        }}
        title={dialog === "start" ? "Confirm your password" : dialog === "disable" ? "Turn off two-step verification?" : "Create new recovery codes?"}
        description={dialog === "start" ? "Changing how you sign in needs your password." : "Needs your password and a current code (or a recovery code)."}
        footer={
          <Button type="submit" form="mfa-dialog" loading={pending} variant={dialog === "disable" ? "destructive" : "primary"}>
            Continue
          </Button>
        }
      >
        <form
          id="mfa-dialog"
          className="space-y-4"
          onSubmit={(e) =>
            run(e, async () => {
              const form = e.currentTarget;
              if (dialog === "start") {
                const result = await api.post<MfaSetupDto>("auth/mfa/totp/setup", { password: value(form, "password") });
                setSetup({ ...result, qr: await QRCode.toDataURL(result.otpAuthUri, { margin: 1, width: 320 }) });
              } else if (dialog === "disable") {
                await api.post("auth/mfa/disable", { password: value(form, "password"), code: value(form, "code") });
                toast({ tone: "success", title: "Two-step verification is off" });
                setCodes(null);
                await refresh();
              } else {
                const result = await api.post<{ recoveryCodes: string[] }>("auth/mfa/recovery-codes", { password: value(form, "password"), code: value(form, "code") });
                setCodes(result.recoveryCodes);
                await refresh();
              }
              setDialog(null);
            })
          }
        >
          <Field label="Password" htmlFor="mfa-password" error={errors.password} required>
            <Input id="mfa-password" name="password" type="password" autoComplete="current-password" required autoFocus />
          </Field>
          {dialog !== "start" ? (
            <Field label="Authentication or recovery code" htmlFor="mfa-code" error={errors.code} required>
              <Input id="mfa-code" name="code" autoComplete="one-time-code" required />
            </Field>
          ) : errors.code ? (
            <Alert tone="error">{errors.code}</Alert>
          ) : null}
        </form>
      </Dialog>
    </Card>
  );
}

function SessionsCard({ initial }: { initial: SessionDto[] }) {
  const { toast } = useToast();
  const [sessions, setSessions] = React.useState(initial);
  const [busy, setBusy] = React.useState<string | null>(null);

  async function reload() {
    setSessions(await api.get<SessionDto[]>("auth/sessions"));
  }

  async function revoke(id: string | "others") {
    setBusy(id);
    try {
      if (id === "others") await api.post("auth/sessions/revoke-others");
      else await api.delete(`auth/sessions/${id}`);
      toast({ tone: "success", title: id === "others" ? "Other sessions signed out" : "Session signed out" });
      await reload();
    } catch (e) {
      toast({ tone: "error", title: "Could not sign out", description: messageFrom(e) });
    } finally {
      setBusy(null);
    }
  }

  return (
    <Card>
      <CardHeader className="flex-row items-start justify-between gap-3">
        <div>
          <CardTitle>Where you are signed in</CardTitle>
          <CardDescription>Active sessions in all your workspaces.</CardDescription>
        </div>
        <Button variant="outline" onClick={() => revoke("others")} loading={busy === "others"} disabled={sessions.length <= 1}>
          Sign out all others
        </Button>
      </CardHeader>
      <CardContent>
        <ul className="divide-y divide-border">
          {sessions.map((s) => (
            <li key={s.id} className="flex items-center gap-3 py-3">
              <Laptop className="h-5 w-5 shrink-0 text-muted-foreground" aria-hidden />
              <div className="min-w-0 flex-1 text-sm">
                <p className="truncate font-medium" title={s.userAgent ?? undefined}>
                  {describeAgent(s.userAgent)}
                  {s.isCurrent ? <Badge tone="success" className="ml-2">This device</Badge> : null}
                  {s.mfaVerified ? <Badge className="ml-1">MFA</Badge> : null}
                </p>
                <p className="text-xs text-muted-foreground">
                  {s.tenantName} · {s.ipAddress ?? "unknown address"} · last active {formatDate(s.lastSeenAtUtc)} · started {formatDate(s.createdAtUtc)}
                </p>
              </div>
              {!s.isCurrent ? (
                <Button variant="ghost" size="sm" onClick={() => revoke(s.id)} loading={busy === s.id}>
                  Sign out
                </Button>
              ) : null}
            </li>
          ))}
        </ul>
      </CardContent>
    </Card>
  );
}

export function describeAgent(userAgent: string | null): string {
  if (!userAgent) return "Unknown device";
  const browser = /Edg\//.test(userAgent) ? "Edge" : /Firefox\//.test(userAgent) ? "Firefox" : /Chrome\//.test(userAgent) ? "Chrome" : /Safari\//.test(userAgent) ? "Safari" : null;
  const os = /Windows/.test(userAgent) ? "Windows" : /Mac OS X|Macintosh/.test(userAgent) ? "macOS" : /Android/.test(userAgent) ? "Android" : /iPhone|iPad/.test(userAgent) ? "iOS" : /Linux/.test(userAgent) ? "Linux" : null;
  return browser && os ? `${browser} on ${os}` : browser ?? os ?? userAgent.slice(0, 40);
}

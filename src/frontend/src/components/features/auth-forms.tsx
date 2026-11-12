"use client";

import * as React from "react";
import Link from "next/link";
import { useRouter, useSearchParams } from "next/navigation";
import { z } from "zod";
import { MailCheck } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Field } from "@/components/ui/field";
import { Alert } from "@/components/ui/alert";
import { authPost } from "@/lib/api/client";
import { isApiError } from "@/lib/api/errors";
import type { MfaChallengeDto, PasswordRulesDto, PublicSession } from "@/lib/api/types";
import { fieldErrorsFrom, messageFrom } from "@/lib/forms";

function issuesToErrors(issues: z.core.$ZodIssue[]): Record<string, string> {
  const next: Record<string, string> = {};
  for (const issue of issues) next[String(issue.path[0])] ??= issue.message;
  return next;
}

/** Only same-site relative paths are followed after sign-in (no open redirect). */
export function safeNext(next: string | null): string {
  return next && next.startsWith("/") && !next.startsWith("//") && !next.startsWith("/\\") ? next : "/dashboard";
}

let capturedToken: { path: string; token: string | null } | null = null;

function readFragmentToken(): string | null {
  const path = window.location.pathname;
  const hash = window.location.hash;
  if (hash || capturedToken?.path !== path) {
    capturedToken = { path, token: new URLSearchParams(hash.replace(/^#/, "")).get("token") };
    // Remove the secret from the address bar and the history entry straight away.
    if (hash) window.history.replaceState(null, "", path + window.location.search);
  }
  return capturedToken.token;
}

const noSubscription = () => () => {};

/**
 * The one-time token from the URL fragment (#token=…), which browsers never
 * send to a server, so it stays out of access logs and Referer headers.
 * `undefined` while rendering on the server, `null` when the link has none.
 */
export function useFragmentToken(): string | null | undefined {
  return React.useSyncExternalStore(noSubscription, readFragmentToken, () => undefined);
}

export function PasswordRulesHint({ rules }: { rules: PasswordRulesDto | null }) {
  if (!rules) return null;
  const parts = [`at least ${rules.minLength} characters`];
  if (rules.requireUppercase) parts.push("an upper-case letter");
  if (rules.requireLowercase) parts.push("a lower-case letter");
  if (rules.requireDigit) parts.push("a digit");
  if (rules.requireSymbol) parts.push("a symbol");
  return <>Use {parts.join(", ")}. Avoid common passwords and your own name.</>;
}

const loginSchema = z.object({
  email: z.email("Enter a valid e-mail address"),
  password: z.string().min(1, "Password is required"),
  tenantSlug: z.string().optional(),
});

export function LoginForm() {
  const router = useRouter();
  const params = useSearchParams();
  const [pending, setPending] = React.useState(false);
  const [formError, setFormError] = React.useState<React.ReactNode>(null);
  const [errors, setErrors] = React.useState<Record<string, string>>({});
  const [challenge, setChallenge] = React.useState<MfaChallengeDto | null>(null);
  const [useRecovery, setUseRecovery] = React.useState(false);
  const reason = params.get("reason");

  function done() {
    router.replace(safeNext(params.get("next")));
    router.refresh();
  }

  async function onSubmit(event: React.FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setFormError(null);
    const data = Object.fromEntries(new FormData(event.currentTarget)) as Record<string, string>;
    const parsed = loginSchema.safeParse({ ...data, tenantSlug: data.tenantSlug || undefined });
    if (!parsed.success) {
      setErrors(issuesToErrors(parsed.error.issues));
      return;
    }
    setErrors({});
    setPending(true);
    try {
      const result = await authPost<{ mfaRequired: boolean; challenge?: MfaChallengeDto; session?: PublicSession }>("/api/auth/login", {
        ...parsed.data,
        tenantSlug: parsed.data.tenantSlug ?? null,
      });
      if (result.mfaRequired && result.challenge) {
        setChallenge(result.challenge);
      } else {
        done();
      }
    } catch (e) {
      setErrors(fieldErrorsFrom(e));
      if (isApiError(e) && e.code === "email_not_verified") {
        setFormError(
          <>
            {e.problem.detail}{" "}
            <Link href="/verify-email" className="font-medium text-primary hover:underline">
              Send a new link
            </Link>
          </>,
        );
      } else if (isApiError(e) && e.code === "password_expired") {
        setFormError(
          <>
            {e.problem.detail}{" "}
            <Link href="/forgot-password" className="font-medium text-primary hover:underline">
              Choose a new password
            </Link>
          </>,
        );
      } else {
        setFormError(messageFrom(e, "Sign-in failed."));
      }
    } finally {
      setPending(false);
    }
  }

  async function onVerify(event: React.FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (!challenge) return;
    const value = String(new FormData(event.currentTarget).get("code") ?? "").trim();
    if (!value) {
      setErrors({ code: "Enter the code" });
      return;
    }
    setErrors({});
    setFormError(null);
    setPending(true);
    try {
      await authPost("/api/auth/mfa", useRecovery
        ? { mfaToken: challenge.mfaToken, code: null, recoveryCode: value }
        : { mfaToken: challenge.mfaToken, code: value, recoveryCode: null });
      done();
    } catch (e) {
      if (isApiError(e) && e.code === "mfa_challenge_invalid") {
        setChallenge(null);
        setFormError("That sign-in attempt has expired. Enter your password again.");
      } else {
        setFormError(messageFrom(e, "The code is not valid."));
      }
      (event.target as HTMLFormElement).reset();
    } finally {
      setPending(false);
    }
  }

  if (challenge) {
    return (
      <form onSubmit={onVerify} className="space-y-5" noValidate>
        <div>
          <h1 className="text-2xl font-semibold">Two-step verification</h1>
          <p className="mt-1 text-sm text-muted-foreground">
            {useRecovery ? "Enter one of your recovery codes. Each code works once." : "Enter the 6-digit code from your authenticator app."}
          </p>
        </div>
        {formError ? <Alert tone="error">{formError}</Alert> : null}
        <Field label={useRecovery ? "Recovery code" : "Authentication code"} htmlFor="code" error={errors.code} required>
          <Input
            key={useRecovery ? "recovery" : "totp"}
            id="code"
            name="code"
            autoComplete="one-time-code"
            inputMode={useRecovery ? "text" : "numeric"}
            placeholder={useRecovery ? "XXXX-XXXX-XXXX" : "123456"}
            maxLength={useRecovery ? 20 : 7}
            autoFocus
            required
          />
        </Field>
        <Button type="submit" className="w-full" loading={pending}>
          Verify
        </Button>
        <div className="flex justify-between text-sm">
          <button type="button" className="text-primary hover:underline" onClick={() => setUseRecovery(!useRecovery)}>
            {useRecovery ? "Use authenticator app" : "Use a recovery code"}
          </button>
          <button type="button" className="text-muted-foreground hover:underline" onClick={() => setChallenge(null)}>
            Start over
          </button>
        </div>
      </form>
    );
  }

  return (
    <form onSubmit={onSubmit} className="space-y-5" noValidate>
      <div>
        <h1 className="text-2xl font-semibold">Sign in</h1>
        <p className="mt-1 text-sm text-muted-foreground">Welcome back. Enter your credentials to continue.</p>
      </div>
      {reason === "expired" ? <Alert tone="info">Your session has ended. Please sign in again.</Alert> : null}
      {reason === "verified" ? <Alert tone="success">Your e-mail address is confirmed. You can sign in now.</Alert> : null}
      {reason === "reset" ? <Alert tone="success">Your password was changed and all sessions were signed out. Sign in with the new password.</Alert> : null}
      {reason === "joined" ? <Alert tone="success">Invitation accepted. Sign in to open the workspace.</Alert> : null}
      {formError ? <Alert tone="error">{formError}</Alert> : null}
      <Field label="E-mail" htmlFor="email" error={errors.email} required>
        <Input id="email" name="email" type="email" autoComplete="username" required aria-invalid={!!errors.email} />
      </Field>
      <Field label="Password" htmlFor="password" error={errors.password} required>
        <Input id="password" name="password" type="password" autoComplete="current-password" required aria-invalid={!!errors.password} />
      </Field>
      <Field label="Workspace" htmlFor="tenantSlug" error={errors.tenantSlug} hint="Optional. Only needed if you belong to several workspaces.">
        <Input id="tenantSlug" name="tenantSlug" placeholder="acme" autoComplete="organization" defaultValue={params.get("workspace") ?? undefined} />
      </Field>
      <Button type="submit" className="w-full" loading={pending}>
        Sign in
      </Button>
      <div className="flex justify-between text-sm text-muted-foreground">
        <Link href="/forgot-password" className="font-medium text-primary hover:underline">
          Forgot password?
        </Link>
        <span>
          New to Solvix?{" "}
          <Link href="/register" className="font-medium text-primary hover:underline">
            Create a workspace
          </Link>
        </span>
      </div>
    </form>
  );
}

const registerSchema = z.object({
  companyName: z.string().trim().min(2, "Company name is required").max(200),
  slug: z
    .string()
    .trim()
    .toLowerCase()
    .regex(/^$|^[a-z0-9](?:[a-z0-9-]{1,61}[a-z0-9])$/, "3-63 lowercase letters, digits or hyphens")
    .refine((s) => !s.includes("--"), "No consecutive hyphens"),
  ownerDisplayName: z.string().trim().min(1, "Your name is required").max(200),
  ownerEmail: z.email("Enter a valid e-mail address"),
  ownerPassword: z.string().min(1, "Choose a password"),
});

export function RegisterForm({ rules }: { rules: PasswordRulesDto | null }) {
  const router = useRouter();
  const [pending, setPending] = React.useState(false);
  const [formError, setFormError] = React.useState<string | null>(null);
  const [errors, setErrors] = React.useState<Record<string, string>>({});
  const [sentTo, setSentTo] = React.useState<string | null>(null);

  async function onSubmit(event: React.FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setFormError(null);
    const data = Object.fromEntries(new FormData(event.currentTarget)) as Record<string, string>;
    const parsed = registerSchema.safeParse(data);
    if (!parsed.success) {
      setErrors(issuesToErrors(parsed.error.issues));
      return;
    }
    setErrors({});
    setPending(true);
    try {
      const result = await authPost<{ emailVerificationRequired: boolean }>("/api/auth/register", { ...parsed.data, slug: parsed.data.slug || null });
      if (result.emailVerificationRequired) {
        setSentTo(parsed.data.ownerEmail);
      } else {
        router.replace("/dashboard");
        router.refresh();
      }
    } catch (e) {
      setErrors(fieldErrorsFrom(e));
      setFormError(messageFrom(e, "Registration failed."));
    } finally {
      setPending(false);
    }
  }

  if (sentTo) {
    return <CheckYourInbox email={sentTo} purpose="confirm your address and finish setting up the workspace" />;
  }

  return (
    <form onSubmit={onSubmit} className="space-y-5" noValidate>
      <div>
        <h1 className="text-2xl font-semibold">Create your workspace</h1>
        <p className="mt-1 text-sm text-muted-foreground">Sets up your company, a headquarters branch and your administrator account.</p>
      </div>
      {formError ? <Alert tone="error">{formError}</Alert> : null}
      <Field label="Company name" htmlFor="companyName" error={errors.companyName} required>
        <Input id="companyName" name="companyName" autoComplete="organization" required aria-invalid={!!errors.companyName} />
      </Field>
      <Field label="Workspace id" htmlFor="slug" error={errors.slug} hint="Lowercase letters, digits and hyphens. Leave empty to derive it from the company name.">
        <Input id="slug" name="slug" placeholder="acme" aria-invalid={!!errors.slug} />
      </Field>
      <div className="grid gap-5 sm:grid-cols-2">
        <Field label="Your name" htmlFor="ownerDisplayName" error={errors.ownerDisplayName} required>
          <Input id="ownerDisplayName" name="ownerDisplayName" autoComplete="name" required aria-invalid={!!errors.ownerDisplayName} />
        </Field>
        <Field label="E-mail" htmlFor="ownerEmail" error={errors.ownerEmail} required>
          <Input id="ownerEmail" name="ownerEmail" type="email" autoComplete="email" required aria-invalid={!!errors.ownerEmail} />
        </Field>
      </div>
      <Field label="Password" htmlFor="ownerPassword" error={errors.ownerPassword} hint={rules ? undefined : "Choose a strong password."} required>
        <Input id="ownerPassword" name="ownerPassword" type="password" autoComplete="new-password" required aria-invalid={!!errors.ownerPassword} />
      </Field>
      {!errors.ownerPassword ? (
        <p className="-mt-3 text-xs text-muted-foreground">
          <PasswordRulesHint rules={rules} />
        </p>
      ) : null}
      <Button type="submit" className="w-full" loading={pending}>
        Create workspace
      </Button>
      <p className="text-center text-sm text-muted-foreground">
        Already have an account?{" "}
        <Link href="/login" className="font-medium text-primary hover:underline">
          Sign in
        </Link>
      </p>
    </form>
  );
}

export function CheckYourInbox({ email, purpose }: { email: string; purpose: string }) {
  return (
    <div className="space-y-5 text-center">
      <MailCheck className="mx-auto h-10 w-10 text-primary" aria-hidden />
      <div>
        <h1 className="text-2xl font-semibold">Check your inbox</h1>
        <p className="mt-2 text-sm text-muted-foreground">
          If <span className="font-medium text-foreground">{email}</span> can receive it, we sent a link to {purpose}. The link works once.
        </p>
      </div>
      <Link href="/login" className="text-sm font-medium text-primary hover:underline">
        Back to sign in
      </Link>
    </div>
  );
}

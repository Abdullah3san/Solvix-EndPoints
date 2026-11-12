"use client";

import * as React from "react";
import Link from "next/link";
import { useRouter } from "next/navigation";
import { z } from "zod";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Field } from "@/components/ui/field";
import { Alert } from "@/components/ui/alert";
import { Skeleton } from "@/components/ui/skeleton";
import { authPost } from "@/lib/api/client";
import type { InvitationAcceptedDto, InvitationPreviewDto, PasswordRulesDto } from "@/lib/api/types";
import { fieldErrorsFrom, messageFrom } from "@/lib/forms";
import { CheckYourInbox, PasswordRulesHint, useFragmentToken } from "./auth-forms";

const emailSchema = z.email("Enter a valid e-mail address");

/** Request form shared by "forgot password" and "resend verification": the answer never reveals whether the address exists. */
function EmailRequestForm({ action, title, description, purpose, button }: { action: string; title: string; description: string; purpose: string; button: string }) {
  const [pending, setPending] = React.useState(false);
  const [error, setError] = React.useState<string | null>(null);
  const [sentTo, setSentTo] = React.useState<string | null>(null);

  async function onSubmit(event: React.FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const email = String(new FormData(event.currentTarget).get("email") ?? "");
    const parsed = emailSchema.safeParse(email);
    if (!parsed.success) {
      setError(parsed.error.issues[0].message);
      return;
    }
    setError(null);
    setPending(true);
    try {
      await authPost(`/api/auth/public/${action}`, { email: parsed.data });
      setSentTo(parsed.data);
    } catch (e) {
      setError(messageFrom(e));
    } finally {
      setPending(false);
    }
  }

  if (sentTo) return <CheckYourInbox email={sentTo} purpose={purpose} />;

  return (
    <form onSubmit={onSubmit} className="space-y-5" noValidate>
      <div>
        <h1 className="text-2xl font-semibold">{title}</h1>
        <p className="mt-1 text-sm text-muted-foreground">{description}</p>
      </div>
      <Field label="E-mail" htmlFor="email" error={error ?? undefined} required>
        <Input id="email" name="email" type="email" autoComplete="email" required autoFocus />
      </Field>
      <Button type="submit" className="w-full" loading={pending}>
        {button}
      </Button>
      <p className="text-center text-sm">
        <Link href="/login" className="font-medium text-primary hover:underline">
          Back to sign in
        </Link>
      </p>
    </form>
  );
}

export function ForgotPasswordForm() {
  return (
    <EmailRequestForm
      action="forgot-password"
      title="Reset your password"
      description="Enter the e-mail address of your account and we will send you a link to choose a new password."
      purpose="choose a new password"
      button="Send reset link"
    />
  );
}

export function ResetPasswordForm({ rules }: { rules: PasswordRulesDto | null }) {
  const router = useRouter();
  const token = useFragmentToken();
  const [pending, setPending] = React.useState(false);
  const [errors, setErrors] = React.useState<Record<string, string>>({});
  const [error, setError] = React.useState<string | null>(null);

  async function onSubmit(event: React.FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const data = new FormData(event.currentTarget);
    const password = String(data.get("newPassword") ?? "");
    if (password !== String(data.get("confirm") ?? "")) {
      setErrors({ confirm: "The passwords do not match." });
      return;
    }
    setErrors({});
    setError(null);
    setPending(true);
    try {
      await authPost("/api/auth/public/reset-password", { token, newPassword: password });
      router.replace("/login?reason=reset");
    } catch (e) {
      const fields = fieldErrorsFrom(e);
      setErrors(fields);
      setError(fields.token ?? (fields.newPassword ? null : messageFrom(e)));
    } finally {
      setPending(false);
    }
  }

  if (token === undefined) return <Skeleton className="h-64 w-full" />;
  if (token === null) return <InvalidLink what="password reset" retry="/forgot-password" />;

  return (
    <form onSubmit={onSubmit} className="space-y-5" noValidate>
      <div>
        <h1 className="text-2xl font-semibold">Choose a new password</h1>
        <p className="mt-1 text-sm text-muted-foreground">You will be signed out everywhere afterwards.</p>
      </div>
      {error ? (
        <Alert tone="error">
          {error}{" "}
          {errors.token ? (
            <Link href="/forgot-password" className="font-medium text-primary hover:underline">
              Request a new link
            </Link>
          ) : null}
        </Alert>
      ) : null}
      <Field label="New password" htmlFor="newPassword" error={errors.newPassword} required>
        <Input id="newPassword" name="newPassword" type="password" autoComplete="new-password" required autoFocus />
      </Field>
      <Field label="Repeat new password" htmlFor="confirm" error={errors.confirm} required>
        <Input id="confirm" name="confirm" type="password" autoComplete="new-password" required />
      </Field>
      <p className="text-xs text-muted-foreground">
        <PasswordRulesHint rules={rules} />
      </p>
      <Button type="submit" className="w-full" loading={pending}>
        Change password
      </Button>
    </form>
  );
}

export function VerifyEmailForm() {
  const router = useRouter();
  const token = useFragmentToken();
  const [state, setState] = React.useState<"working" | "failed">("working");
  const started = React.useRef(false);

  React.useEffect(() => {
    if (!token || started.current) return;
    started.current = true;
    authPost("/api/auth/public/verify-email", { token })
      .then(() => router.replace("/login?reason=verified"))
      .catch(() => setState("failed"));
  }, [token, router]);

  if (token === undefined || (token && state === "working")) {
    return <p className="text-center text-sm text-muted-foreground">Confirming your e-mail address…</p>;
  }

  return (
    <div className="space-y-6">
      {token ? <Alert tone="error">This confirmation link is no longer valid. Links work once and expire after a day.</Alert> : null}
      <EmailRequestForm
        action="resend-verification"
        title="Confirm your e-mail address"
        description="Enter your e-mail address and we will send a new confirmation link."
        purpose="confirm your address"
        button="Send confirmation link"
      />
    </div>
  );
}

export function AcceptInvitationForm() {
  const router = useRouter();
  const token = useFragmentToken();
  const [preview, setPreview] = React.useState<InvitationPreviewDto | null>(null);
  const [failed, setFailed] = React.useState(false);
  const [pending, setPending] = React.useState(false);
  const [errors, setErrors] = React.useState<Record<string, string>>({});
  const [error, setError] = React.useState<string | null>(null);

  const invalid = token === null || failed;

  React.useEffect(() => {
    if (!token) return;
    authPost<InvitationPreviewDto>("/api/auth/public/invitation-preview", { token })
      .then(setPreview)
      .catch(() => setFailed(true));
  }, [token]);

  async function onSubmit(event: React.FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (!preview) return;
    const data = new FormData(event.currentTarget);
    const password = String(data.get("password") ?? "");
    if (!preview.accountExists && password !== String(data.get("confirm") ?? "")) {
      setErrors({ confirm: "The passwords do not match." });
      return;
    }
    setErrors({});
    setError(null);
    setPending(true);
    try {
      const result = await authPost<InvitationAcceptedDto>("/api/auth/public/invitation-accept", {
        token,
        displayName: preview.accountExists ? null : String(data.get("displayName") ?? ""),
        password: preview.accountExists ? null : password,
      });
      router.replace(`/login?reason=joined&workspace=${encodeURIComponent(result.tenantSlug)}`);
    } catch (e) {
      const fields = fieldErrorsFrom(e);
      setErrors(fields);
      if (fields.token) setFailed(true);
      else if (!fields.password && !fields.displayName) setError(messageFrom(e));
    } finally {
      setPending(false);
    }
  }

  if (invalid) return <InvalidLink what="invitation" />;
  if (!preview) return <Skeleton className="h-64 w-full" />;

  return (
    <form onSubmit={onSubmit} className="space-y-5" noValidate>
      <div>
        <h1 className="text-2xl font-semibold">Join {preview.tenantName}</h1>
        <p className="mt-1 text-sm text-muted-foreground">
          You were invited as <span className="font-medium text-foreground">{preview.email}</span> with the role
          {preview.roles.length > 1 ? "s" : ""} {preview.roles.join(", ")}.
        </p>
      </div>
      {error ? <Alert tone="error">{error}</Alert> : null}
      {preview.accountExists ? (
        <Alert tone="info">
          You already have a Solvix account. Accept to add this workspace to it, then sign in with your existing password.
        </Alert>
      ) : (
        <>
          <Field label="Your name" htmlFor="displayName" error={errors.displayName} required>
            <Input id="displayName" name="displayName" autoComplete="name" defaultValue={preview.displayName ?? ""} required />
          </Field>
          <Field label="Password" htmlFor="password" error={errors.password} required>
            <Input id="password" name="password" type="password" autoComplete="new-password" required />
          </Field>
          <Field label="Repeat password" htmlFor="confirm" error={errors.confirm} required>
            <Input id="confirm" name="confirm" type="password" autoComplete="new-password" required />
          </Field>
          <p className="text-xs text-muted-foreground">
            <PasswordRulesHint rules={preview.passwordRules} />
          </p>
        </>
      )}
      <Button type="submit" className="w-full" loading={pending}>
        Accept invitation
      </Button>
    </form>
  );
}

function InvalidLink({ what, retry }: { what: string; retry?: string }) {
  return (
    <div className="space-y-4">
      <h1 className="text-2xl font-semibold">Link not valid</h1>
      <Alert tone="error">This {what} link has expired, was already used or was replaced by a newer one.</Alert>
      <p className="text-sm">
        {retry ? (
          <Link href={retry} className="font-medium text-primary hover:underline">
            Request a new link
          </Link>
        ) : (
          "Ask your administrator to send a new invitation."
        )}
      </p>
    </div>
  );
}

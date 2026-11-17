"use client";

import * as React from "react";
import { useRouter, useSearchParams } from "next/navigation";
import { z } from "zod";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Field } from "@/components/ui/field";
import { Alert } from "@/components/ui/alert";
import type { ProblemDetails } from "@/lib/api/types";

async function submitJson(url: string, body: unknown): Promise<{ ok: true } | { ok: false; problem: ProblemDetails }> {
  const response = await fetch(url, {
    method: "POST",
    headers: { "content-type": "application/json", accept: "application/json" },
    body: JSON.stringify(body),
  });
  if (response.ok) return { ok: true };
  let problem: ProblemDetails = { status: response.status, title: response.statusText };
  try {
    problem = (await response.json()) as ProblemDetails;
  } catch {
    // keep default
  }
  return { ok: false, problem };
}

function useFieldErrors() {
  const [errors, setErrors] = React.useState<Record<string, string>>({});
  const apply = (problem: ProblemDetails) => {
    const next: Record<string, string> = {};
    for (const [key, messages] of Object.entries(problem.errors ?? {})) next[key] = messages[0];
    setErrors(next);
  };
  return { errors, setErrors, apply };
}

function safeNext(next: string | null): string {
  return next && next.startsWith("/") && !next.startsWith("//") ? next : "/dashboard";
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
  const [formError, setFormError] = React.useState<string | null>(null);
  const { errors, setErrors, apply } = useFieldErrors();
  const reason = params.get("reason");

  async function onSubmit(event: React.FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setFormError(null);
    const data = Object.fromEntries(new FormData(event.currentTarget)) as Record<string, string>;
    const parsed = loginSchema.safeParse({ ...data, tenantSlug: data.tenantSlug || undefined });
    if (!parsed.success) {
      const next: Record<string, string> = {};
      for (const issue of parsed.error.issues) next[String(issue.path[0])] = issue.message;
      setErrors(next);
      return;
    }
    setErrors({});
    setPending(true);
    const result = await submitJson("/api/auth/login", { ...parsed.data, tenantSlug: parsed.data.tenantSlug ?? null });
    setPending(false);
    if (result.ok) {
      router.replace(safeNext(params.get("next")));
      router.refresh();
      return;
    }
    apply(result.problem);
    setFormError(result.problem.detail ?? result.problem.title ?? "Sign-in failed.");
  }

  return (
    <form onSubmit={onSubmit} className="space-y-5" noValidate>
      <div>
        <h1 className="text-2xl font-semibold">Sign in</h1>
        <p className="mt-1 text-sm text-muted-foreground">Welcome back. Enter your credentials to continue.</p>
      </div>
      {reason === "expired" ? <Alert tone="info">Your session has expired. Please sign in again.</Alert> : null}
      {formError ? <Alert tone="error">{formError}</Alert> : null}
      <Field label="E-mail" htmlFor="email" error={errors.email} required>
        <Input id="email" name="email" type="email" autoComplete="email" required aria-invalid={!!errors.email} />
      </Field>
      <Field label="Password" htmlFor="password" error={errors.password} required>
        <Input id="password" name="password" type="password" autoComplete="current-password" required aria-invalid={!!errors.password} />
      </Field>
      <Field label="Workspace" htmlFor="tenantSlug" error={errors.tenantSlug} hint="Optional. Only needed if you belong to several workspaces.">
        <Input id="tenantSlug" name="tenantSlug" placeholder="acme" autoComplete="organization" />
      </Field>
      <Button type="submit" className="w-full" loading={pending}>
        Sign in
      </Button>
      <p className="text-center text-sm text-muted-foreground">
        Access is by subscription. Your workspace and account are created for you; contact your administrator or Solvix if you cannot sign in.
      </p>
    </form>
  );
}

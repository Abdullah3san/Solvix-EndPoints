"use client";

import * as React from "react";
import { AlertTriangle, ExternalLink, FileText } from "lucide-react";
import { api, proxyUrl } from "@/lib/api/client";
import type {
  BillingHistoryItemDto,
  BillingInterval,
  BillingProfileDto,
  ChangePlanResult,
  CheckoutDto,
  InvoiceDto,
  PagedResult,
  PlanDto,
  SubscriptionDto,
  UsageDto,
} from "@/lib/api/types";
import { annualSavingsMonths, daysUntil, formatMoney, invoiceTone, isSafeCheckoutUrl, planPrice, subscriptionTone, usageTone } from "@/lib/billing";
import { emptyToNull, fieldErrorsFrom, messageFrom } from "@/lib/forms";
import { Alert } from "@/components/ui/alert";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Field } from "@/components/ui/field";
import { Input } from "@/components/ui/input";
import { Table, THead, TBody, TR, TH, TD, EmptyRow } from "@/components/ui/table";
import { useToast } from "@/components/providers/toast-provider";
import { useCan } from "@/components/providers/session-provider";

interface Props {
  subscription: SubscriptionDto;
  usage: UsageDto;
  plans: PlanDto[];
  /** Owner-only data; null for other roles (the API refuses it anyway). */
  invoices: PagedResult<InvoiceDto> | null;
  profile: BillingProfileDto | null;
  history: BillingHistoryItemDto[];
  checkoutResult: "success" | "cancelled" | null;
}

export function BillingView({ subscription: initialSub, usage: initialUsage, plans, invoices: initialInvoices, profile, history: initialHistory, checkoutResult }: Props) {
  const { toast } = useToast();
  const { isOwner } = useCan();
  const [subscription, setSubscription] = React.useState(initialSub);
  const [usage, setUsage] = React.useState(initialUsage);
  const [invoices, setInvoices] = React.useState(initialInvoices);
  const [history, setHistory] = React.useState(initialHistory);
  const [interval, setIntervalChoice] = React.useState<BillingInterval>("Monthly");
  const [busy, setBusy] = React.useState<string | null>(null);

  async function refresh() {
    const [sub, use] = await Promise.all([api.get<SubscriptionDto>("billing/subscription"), api.get<UsageDto>("billing/usage")]);
    setSubscription(sub);
    setUsage(use);
    if (isOwner) {
      const [inv, hist] = await Promise.all([api.get<PagedResult<InvoiceDto>>("billing/invoices", { pageSize: 25 }), api.get<BillingHistoryItemDto[]>("billing/history")]);
      setInvoices(inv);
      setHistory(hist);
    }
  }

  function goToCheckout(url: string | null | undefined, invoiceNumber: string) {
    if (isSafeCheckoutUrl(url)) {
      window.location.assign(url);
    } else {
      toast({ tone: "success", title: `Invoice ${invoiceNumber} issued`, description: "Pay it by bank transfer using the invoice number as reference. Your plan is activated when payment is confirmed." });
    }
  }

  async function choose(plan: PlanDto) {
    setBusy(plan.code);
    try {
      const result = await api.post<ChangePlanResult>("billing/subscription/change", { planCode: plan.code, interval });
      if (result.outcome === "invoice_issued" && result.invoice) {
        goToCheckout(result.checkoutUrl, result.invoice.number);
      } else if (result.outcome === "scheduled") {
        toast({ tone: "success", title: "Change scheduled", description: `The ${plan.name} plan starts at the end of the current period.` });
      } else {
        toast({ tone: "success", title: "Scheduled change cancelled" });
      }
      await refresh();
    } catch (e) {
      toast({ tone: "error", title: "Could not change plan", description: messageFrom(e) });
    } finally {
      setBusy(null);
    }
  }

  async function pay(invoice: InvoiceDto) {
    setBusy(invoice.id);
    try {
      const checkout = await api.post<CheckoutDto>(`billing/invoices/${invoice.id}/checkout`);
      goToCheckout(checkout.checkoutUrl, invoice.number);
    } catch (e) {
      toast({ tone: "error", title: "Could not start payment", description: messageFrom(e) });
    } finally {
      setBusy(null);
    }
  }

  async function toggleCancel() {
    setBusy("cancel");
    try {
      setSubscription(await api.post<SubscriptionDto>(subscription.cancelAtPeriodEnd ? "billing/subscription/resume" : "billing/subscription/cancel"));
      toast({ tone: "success", title: subscription.cancelAtPeriodEnd ? "Cancellation reverted" : "Subscription will end at the period end" });
    } catch (e) {
      toast({ tone: "error", title: "Could not update subscription", description: messageFrom(e) });
    } finally {
      setBusy(null);
    }
  }

  const trialDays = subscription.status === "Trialing" ? daysUntil(subscription.trialEndsAtUtc) : null;
  const openInvoices = invoices?.items.filter((i) => i.status === "Open") ?? [];

  return (
    <div className="space-y-6">
      {checkoutResult === "success" ? (
        <Alert tone="success" title="Payment received">
          Thank you. Your subscription updates as soon as the payment provider confirms it (usually within a minute).
        </Alert>
      ) : null}
      {checkoutResult === "cancelled" ? <Alert tone="info">Checkout was cancelled. Nothing was charged.</Alert> : null}
      {!subscription.canEnrollDevices ? (
        <Alert tone="error" title="New devices cannot be enrolled">
          {subscription.restrictionMessage ?? "Your subscription does not allow enrolling devices."} Devices that are already enrolled keep working.
        </Alert>
      ) : null}

      <div className="grid gap-4 lg:grid-cols-2">
        <Card>
          <CardHeader>
            <CardTitle className="flex flex-wrap items-center gap-2">
              {subscription.planName || "No plan"} <Badge tone={subscriptionTone(subscription.status)}>{subscription.status === "PastDue" ? "Payment overdue" : subscription.status}</Badge>
            </CardTitle>
            <CardDescription>
              {subscription.status === "Trialing" && trialDays !== null
                ? `Trial ends in ${Math.max(0, trialDays)} day${trialDays === 1 ? "" : "s"}.`
                : subscription.status === "Active" || subscription.status === "PastDue"
                  ? `${formatMoney(subscription.unitPriceMinor, subscription.currency)} per ${subscription.interval === "Monthly" ? "month" : "year"}`
                  : "Choose a plan below to continue."}
            </CardDescription>
          </CardHeader>
          <CardContent className="space-y-2 text-sm">
            <p>
              Current period: {new Date(subscription.currentPeriodStartUtc).toLocaleDateString()} – {new Date(subscription.currentPeriodEndUtc).toLocaleDateString()}
            </p>
            {subscription.graceEndsAtUtc ? (
              <p className="flex items-center gap-1 text-warning">
                <AlertTriangle className="h-4 w-4" /> Pay before {new Date(subscription.graceEndsAtUtc).toLocaleDateString()} to keep enrolling devices.
              </p>
            ) : null}
            {subscription.pendingPlanCode ? (
              <p>
                Scheduled: <strong>{subscription.pendingPlanCode}</strong> ({subscription.pendingInterval}) from {new Date(subscription.currentPeriodEndUtc).toLocaleDateString()}.
              </p>
            ) : null}
            {subscription.cancelAtPeriodEnd ? <p className="text-destructive">Ends on {new Date(subscription.currentPeriodEndUtc).toLocaleDateString()}.</p> : null}
            {isOwner && (subscription.status === "Active" || subscription.status === "Trialing") ? (
              <Button variant="outline" size="sm" loading={busy === "cancel"} onClick={toggleCancel}>
                {subscription.cancelAtPeriodEnd ? "Keep my subscription" : "Cancel at period end"}
              </Button>
            ) : null}
          </CardContent>
        </Card>

        <Card>
          <CardHeader>
            <CardTitle>Device usage</CardTitle>
            <CardDescription>
              {usage.devicesUsed} of {usage.deviceLimit} devices
            </CardDescription>
          </CardHeader>
          <CardContent className="space-y-3">
            <div className="h-3 w-full overflow-hidden rounded-full bg-muted" role="progressbar" aria-valuenow={usage.percentUsed} aria-valuemin={0} aria-valuemax={100} aria-label="Device usage">
              <div className={`h-full ${usage.level === "limit" ? "bg-destructive" : usage.level === "critical" ? "bg-warning" : "bg-primary"}`} style={{ width: `${usage.percentUsed}%` }} />
            </div>
            <div className="flex items-center justify-between text-sm">
              <Badge tone={usageTone(usage.level)}>{usage.level === "limit" ? "Limit reached" : usage.level === "critical" ? "Almost full" : usage.level === "warning" ? "Filling up" : "Plenty of room"}</Badge>
              <span className="text-muted-foreground">{usage.percentUsed}%</span>
            </div>
            {usage.level === "limit" || usage.level === "critical" ? (
              <p className="text-sm text-muted-foreground">Retired devices free their slot. Upgrade to enroll more.</p>
            ) : null}
          </CardContent>
        </Card>
      </div>

      <section aria-labelledby="plans-heading">
        <div className="mb-3 flex flex-wrap items-center justify-between gap-2">
          <h2 id="plans-heading" className="text-lg font-semibold">
            Plans
          </h2>
          <div className="inline-flex rounded-md border border-border p-0.5" role="group" aria-label="Billing interval">
            {(["Monthly", "Annual"] as const).map((i) => (
              <Button key={i} size="sm" variant={interval === i ? "primary" : "ghost"} aria-pressed={interval === i} onClick={() => setIntervalChoice(i)}>
                {i}
              </Button>
            ))}
          </div>
        </div>
        <div className="grid gap-4 md:grid-cols-2 xl:grid-cols-4">
          {plans.map((plan) => {
            const price = planPrice(plan, interval);
            const current = subscription.planCode === plan.code && subscription.status === "Active" && subscription.interval === interval;
            const saving = interval === "Annual" ? annualSavingsMonths(plan) : 0;
            return (
              <Card key={plan.id} className={current ? "border-primary" : undefined}>
                <CardHeader>
                  <CardTitle>{plan.name}</CardTitle>
                  <CardDescription>{plan.isCustom ? "Custom device limit" : `Up to ${plan.deviceLimit} devices`}</CardDescription>
                </CardHeader>
                <CardContent className="space-y-3">
                  <p className="text-2xl font-semibold">
                    {plan.isCustom ? "Custom" : formatMoney(price, plan.currency)}
                    {!plan.isCustom ? <span className="text-sm font-normal text-muted-foreground"> / {interval === "Monthly" ? "mo" : "yr"}</span> : null}
                  </p>
                  {saving > 0 ? <p className="text-xs text-success">Saves {saving} months</p> : null}
                  {plan.isCustom ? (
                    <p className="text-sm text-muted-foreground">Tailored limits and pricing. Contact sales to set it up for your workspace.</p>
                  ) : isOwner ? (
                    <Button className="w-full" variant={current ? "outline" : "primary"} disabled={current} loading={busy === plan.code} onClick={() => choose(plan)}>
                      {current ? "Current plan" : subscription.status === "Active" ? "Switch to this plan" : "Choose plan"}
                    </Button>
                  ) : (
                    <p className="text-xs text-muted-foreground">Only the workspace owner can change the plan.</p>
                  )}
                </CardContent>
              </Card>
            );
          })}
        </div>
      </section>

      {isOwner ? (
        <>
          {openInvoices.length > 0 ? (
            <Alert tone="info" title={`${openInvoices.length} unpaid invoice${openInvoices.length > 1 ? "s" : ""}`}>
              Pay online below, or by bank transfer quoting the invoice number.
            </Alert>
          ) : null}
          <Card>
            <CardHeader>
              <CardTitle>Invoices</CardTitle>
            </CardHeader>
            <Table>
              <THead>
                <TR>
                  <TH>Number</TH>
                  <TH>Issued</TH>
                  <TH>Plan</TH>
                  <TH>Total</TH>
                  <TH>Status</TH>
                  <TH className="text-right">Actions</TH>
                </TR>
              </THead>
              <TBody>
                {!invoices || invoices.items.length === 0 ? (
                  <EmptyRow colSpan={6}>No invoices yet.</EmptyRow>
                ) : (
                  invoices.items.map((i) => (
                    <TR key={i.id}>
                      <TD className="font-mono text-xs">{i.number}</TD>
                      <TD>{new Date(i.issuedAtUtc).toLocaleDateString()}</TD>
                      <TD>
                        {i.planCode} · {i.interval}
                      </TD>
                      <TD>{formatMoney(i.totalMinor, i.currency)}</TD>
                      <TD>
                        <Badge tone={invoiceTone(i.status)}>{i.status}</Badge>
                      </TD>
                      <TD>
                        <div className="flex justify-end gap-1">
                          <a href={proxyUrl(`billing/invoices/${i.id}/html`)} target="_blank" rel="noopener noreferrer" className="inline-flex h-8 items-center gap-1 rounded-md px-2 text-xs hover:bg-muted">
                            <FileText className="h-3.5 w-3.5" /> View
                          </a>
                          {i.status === "Open" ? (
                            <Button size="sm" loading={busy === i.id} onClick={() => pay(i)}>
                              <ExternalLink className="h-3.5 w-3.5" /> Pay
                            </Button>
                          ) : null}
                        </div>
                      </TD>
                    </TR>
                  ))
                )}
              </TBody>
            </Table>
          </Card>

          <ProfileForm initial={profile} />

          <Card>
            <CardHeader>
              <CardTitle>Billing history</CardTitle>
            </CardHeader>
            <CardContent>
              <ul className="space-y-1.5 text-sm">
                {history.length === 0 ? <li className="text-muted-foreground">Nothing yet.</li> : null}
                {history.map((h, idx) => (
                  <li key={`${h.kind}-${h.atUtc}-${idx}`} className="flex flex-wrap items-baseline gap-x-3">
                    <span className="w-28 shrink-0 text-muted-foreground">{new Date(h.atUtc).toLocaleDateString()}</span>
                    <span>{h.title.replace(/([A-Z])/g, " $1").trim()}</span>
                    {h.amountMinor !== null ? <span className="text-muted-foreground">{formatMoney(h.amountMinor, h.currency ?? "USD")}</span> : null}
                    {h.status ? <Badge>{h.status}</Badge> : null}
                  </li>
                ))}
              </ul>
            </CardContent>
          </Card>
        </>
      ) : (
        <Alert tone="info">Invoices and billing details are visible to the workspace owner only.</Alert>
      )}
    </div>
  );
}

function ProfileForm({ initial }: { initial: BillingProfileDto | null }) {
  const { toast } = useToast();
  const [form, setForm] = React.useState<BillingProfileDto>(
    initial ?? { legalName: "", billingEmail: "", addressLine1: "", addressLine2: "", city: "", region: "", postalCode: "", countryCode: "", taxId: "" },
  );
  const [errors, setErrors] = React.useState<Record<string, string>>({});
  const [pending, setPending] = React.useState(false);
  const set = (key: keyof BillingProfileDto) => (e: React.ChangeEvent<HTMLInputElement>) => setForm((f) => ({ ...f, [key]: e.target.value }));

  async function submit(event: React.FormEvent) {
    event.preventDefault();
    setPending(true);
    setErrors({});
    try {
      await api.put("billing/profile", {
        ...form,
        addressLine1: emptyToNull(form.addressLine1),
        addressLine2: emptyToNull(form.addressLine2),
        city: emptyToNull(form.city),
        region: emptyToNull(form.region),
        postalCode: emptyToNull(form.postalCode),
        countryCode: emptyToNull(form.countryCode),
        taxId: emptyToNull(form.taxId),
      });
      toast({ tone: "success", title: "Billing details saved" });
    } catch (e) {
      setErrors(fieldErrorsFrom(e));
      toast({ tone: "error", title: "Could not save", description: messageFrom(e) });
    } finally {
      setPending(false);
    }
  }

  return (
    <Card>
      <CardHeader>
        <CardTitle>Billing details</CardTitle>
        <CardDescription>Printed on your invoices. Visible to the workspace owner only.</CardDescription>
      </CardHeader>
      <CardContent>
        <form onSubmit={submit} className="grid gap-4 sm:grid-cols-2">
          <Field label="Legal name" htmlFor="b-name" error={errors.legalName} required>
            <Input id="b-name" value={form.legalName} onChange={set("legalName")} maxLength={200} required />
          </Field>
          <Field label="Billing e-mail" htmlFor="b-email" error={errors.billingEmail} required>
            <Input id="b-email" type="email" value={form.billingEmail} onChange={set("billingEmail")} required />
          </Field>
          <Field label="Address" htmlFor="b-a1" error={errors.addressLine1}>
            <Input id="b-a1" value={form.addressLine1 ?? ""} onChange={set("addressLine1")} />
          </Field>
          <Field label="Address line 2" htmlFor="b-a2" error={errors.addressLine2}>
            <Input id="b-a2" value={form.addressLine2 ?? ""} onChange={set("addressLine2")} />
          </Field>
          <Field label="City" htmlFor="b-city" error={errors.city}>
            <Input id="b-city" value={form.city ?? ""} onChange={set("city")} />
          </Field>
          <Field label="Region" htmlFor="b-region" error={errors.region}>
            <Input id="b-region" value={form.region ?? ""} onChange={set("region")} />
          </Field>
          <Field label="Postal code" htmlFor="b-zip" error={errors.postalCode}>
            <Input id="b-zip" value={form.postalCode ?? ""} onChange={set("postalCode")} />
          </Field>
          <Field label="Country (ISO code)" htmlFor="b-country" error={errors.countryCode}>
            <Input id="b-country" value={form.countryCode ?? ""} onChange={set("countryCode")} maxLength={2} />
          </Field>
          <Field label="Tax / VAT ID" htmlFor="b-tax" error={errors.taxId}>
            <Input id="b-tax" value={form.taxId ?? ""} onChange={set("taxId")} />
          </Field>
          <div className="flex items-end">
            <Button type="submit" loading={pending}>
              Save details
            </Button>
          </div>
        </form>
      </CardContent>
    </Card>
  );
}

import type { Metadata } from "next";
import { serverApi } from "@/lib/api/server";
import { ApiError } from "@/lib/api/errors";
import type { BillingHistoryItemDto, BillingProfileDto, InvoiceDto, PagedResult, PlanDto, SubscriptionDto, UsageDto } from "@/lib/api/types";
import { PageHeader } from "@/components/ui/page-header";
import { BillingView } from "@/components/features/billing/billing-view";

export const metadata: Metadata = { title: "Billing" };

async function ownerOnly<T>(path: string): Promise<T | null> {
  try {
    return await serverApi<T>(path, { redirectOnUnauthorized: false });
  } catch (e) {
    // Invoices, profile and history belong to the workspace owner; everyone else just gets the plan and usage.
    if (e instanceof ApiError && e.status === 403) return null;
    throw e;
  }
}

export default async function BillingPage({ searchParams }: { searchParams: Promise<{ checkout?: string }> }) {
  const { checkout } = await searchParams;
  const [subscription, usage, plans, invoices, profile, history] = await Promise.all([
    serverApi<SubscriptionDto>("billing/subscription"),
    serverApi<UsageDto>("billing/usage"),
    serverApi<PlanDto[]>("billing/plans"),
    ownerOnly<PagedResult<InvoiceDto>>("billing/invoices?pageSize=25"),
    ownerOnly<BillingProfileDto>("billing/profile"),
    ownerOnly<BillingHistoryItemDto[]>("billing/history"),
  ]);
  return (
    <>
      <PageHeader title="Billing" description="Your plan, device usage, invoices and billing details." />
      <BillingView
        subscription={subscription}
        usage={usage}
        plans={plans}
        invoices={invoices}
        profile={profile}
        history={history ?? []}
        checkoutResult={checkout === "success" || checkout === "cancelled" ? checkout : null}
      />
    </>
  );
}

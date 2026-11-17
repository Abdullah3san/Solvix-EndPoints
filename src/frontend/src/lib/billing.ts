import type { BillingInterval, InvoiceStatus, PlanDto, SubscriptionStatus, UsageDto } from "@/lib/api/types";

type Tone = "neutral" | "brand" | "success" | "warning" | "danger";

/** Amounts are integers in minor units (cents). Never use floating point for money. */
export function formatMoney(minor: number | null | undefined, currency = "USD"): string {
  if (minor === null || minor === undefined || !Number.isFinite(minor)) return "—";
  const sign = minor < 0 ? "-" : "";
  const abs = Math.abs(minor);
  const major = Math.floor(abs / 100);
  const cents = String(abs % 100).padStart(2, "0");
  const grouped = major.toString().replace(/\B(?=(\d{3})+(?!\d))/g, ",");
  return `${sign}${currency === "USD" ? "$" : ""}${grouped}.${cents}${currency === "USD" ? "" : ` ${currency}`}`;
}

export function planPrice(plan: PlanDto, interval: BillingInterval): number | null {
  return interval === "Monthly" ? plan.monthlyPriceMinor : plan.annualPriceMinor;
}

/** Whole months of savings the annual price gives against twelve monthly payments (0 when none). */
export function annualSavingsMonths(plan: PlanDto): number {
  if (!plan.monthlyPriceMinor || !plan.annualPriceMinor) return 0;
  return Math.max(0, Math.round((plan.monthlyPriceMinor * 12 - plan.annualPriceMinor) / plan.monthlyPriceMinor));
}

export function subscriptionTone(status: SubscriptionStatus): Tone {
  switch (status) {
    case "Active":
      return "success";
    case "Trialing":
      return "brand";
    case "PastDue":
      return "warning";
    default:
      return "danger";
  }
}

export function invoiceTone(status: InvoiceStatus): Tone {
  return status === "Paid" ? "success" : status === "Open" ? "warning" : "neutral";
}

export function usageTone(level: UsageDto["level"]): Tone {
  switch (level) {
    case "limit":
      return "danger";
    case "critical":
      return "warning";
    case "warning":
      return "brand";
    default:
      return "success";
  }
}

export function daysUntil(value: string | null | undefined, now: Date = new Date()): number | null {
  if (!value) return null;
  const ms = new Date(value).getTime() - now.getTime();
  return Number.isNaN(ms) ? null : Math.ceil(ms / 86_400_000);
}

/** Decides whether a checkout URL is safe to navigate to: https only (a provider URL, never user-supplied script). */
export function isSafeCheckoutUrl(url: string | null | undefined): url is string {
  if (!url) return false;
  try {
    return new URL(url).protocol === "https:";
  } catch {
    return false;
  }
}

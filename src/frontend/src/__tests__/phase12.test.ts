import { describe, expect, it } from "vitest";
import { daysToMask, formatDue, formatMinutes, maskToDays, minuteToTime, priorityTone, slaLabel, slaTone, statusLabel, statusTone, timeToMinute } from "@/lib/helpdesk";
import { annualSavingsMonths, daysUntil, formatMoney, invoiceTone, isSafeCheckoutUrl, planPrice, subscriptionTone, usageTone } from "@/lib/billing";
import { formatCell, formatMetric } from "@/lib/reports";
import type { PlanDto } from "@/lib/api/types";

const business: PlanDto = { id: "1", code: "business", name: "Business", description: null, deviceLimit: 75, monthlyPriceMinor: 12900, annualPriceMinor: 129000, currency: "USD", isCustom: false };

describe("helpdesk helpers", () => {
  it("labels and tones statuses", () => {
    expect(statusLabel("WaitingOnRequester")).toBe("Waiting on requester");
    expect(statusLabel("InProgress")).toBe("In progress");
    expect(statusLabel("Open")).toBe("Open");
    expect(statusTone("Resolved")).toBe("success");
    expect(statusTone("New")).toBe("brand");
  });

  it("tones priorities and SLA states", () => {
    expect(priorityTone("Critical")).toBe("danger");
    expect(priorityTone("Low")).toBe("neutral");
    expect(slaTone("breached")).toBe("danger");
    expect(slaTone("at_risk")).toBe("warning");
    expect(slaTone("paused")).toBe("neutral");
    expect(slaLabel("at_risk")).toBe("At risk");
    expect(slaLabel("none")).toBe("No SLA");
  });

  it("counts down to and past an SLA deadline", () => {
    const now = new Date("2026-11-16T12:00:00Z");
    expect(formatDue("2026-11-16T14:30:00Z", now)).toBe("2h 30m left");
    expect(formatDue("2026-11-16T12:20:00Z", now)).toBe("20m left");
    expect(formatDue("2026-11-18T14:00:00Z", now)).toBe("2d 2h left");
    expect(formatDue("2026-11-15T09:30:00Z", now)).toBe("overdue by 1d 2h");
    expect(formatDue(null, now)).toBe("—");
    expect(formatDue("nonsense", now)).toBe("—");
  });

  it("formats SLA target durations", () => {
    expect(formatMinutes(30)).toBe("30m");
    expect(formatMinutes(90)).toBe("1h 30m");
    expect(formatMinutes(480)).toBe("8h");
    expect(formatMinutes(4320)).toBe("3d");
    expect(formatMinutes(-1)).toBe("—");
  });

  it("converts working-day masks and times", () => {
    expect(maskToDays(0b0111110)).toEqual([1, 2, 3, 4, 5]);
    expect(daysToMask([1, 2, 3, 4, 5])).toBe(62);
    expect(daysToMask(maskToDays(127))).toBe(127);
    expect(minuteToTime(540)).toBe("09:00");
    expect(minuteToTime(1020)).toBe("17:00");
    expect(timeToMinute("09:30")).toBe(570);
    expect(timeToMinute("24:00")).toBe(1440);
    expect(timeToMinute("25:00")).toBeNull();
    expect(timeToMinute("bad")).toBeNull();
  });
});

describe("billing helpers", () => {
  it("formats money from minor units without floating point", () => {
    expect(formatMoney(12900)).toBe("$129.00");
    expect(formatMoney(4900)).toBe("$49.00");
    expect(formatMoney(5)).toBe("$0.05");
    expect(formatMoney(123456789)).toBe("$1,234,567.89");
    expect(formatMoney(-6450)).toBe("-$64.50");
    expect(formatMoney(1000, "EUR")).toBe("10.00 EUR");
    expect(formatMoney(null)).toBe("—");
    expect(formatMoney(Number.NaN)).toBe("—");
  });

  it("picks plan prices and shows the annual saving", () => {
    expect(planPrice(business, "Monthly")).toBe(12900);
    expect(planPrice(business, "Annual")).toBe(129000);
    expect(annualSavingsMonths(business)).toBe(2);
    expect(annualSavingsMonths({ ...business, annualPriceMinor: null })).toBe(0);
    expect(annualSavingsMonths({ ...business, annualPriceMinor: 999999 })).toBe(0);
  });

  it("maps statuses and usage levels to tones", () => {
    expect(subscriptionTone("Active")).toBe("success");
    expect(subscriptionTone("Trialing")).toBe("brand");
    expect(subscriptionTone("PastDue")).toBe("warning");
    expect(subscriptionTone("Expired")).toBe("danger");
    expect(subscriptionTone("Suspended")).toBe("danger");
    expect(invoiceTone("Paid")).toBe("success");
    expect(invoiceTone("Open")).toBe("warning");
    expect(usageTone("limit")).toBe("danger");
    expect(usageTone("critical")).toBe("warning");
    expect(usageTone("ok")).toBe("success");
  });

  it("counts days until a date", () => {
    const now = new Date("2026-11-16T12:00:00Z");
    expect(daysUntil("2026-11-19T12:00:00Z", now)).toBe(3);
    expect(daysUntil("2026-11-16T13:00:00Z", now)).toBe(1);
    expect(daysUntil("2026-11-10T12:00:00Z", now)).toBe(-6);
    expect(daysUntil(null, now)).toBeNull();
  });

  it("only follows https checkout URLs", () => {
    expect(isSafeCheckoutUrl("https://checkout.stripe.com/c/pay/abc")).toBe(true);
    expect(isSafeCheckoutUrl("http://checkout.example/c")).toBe(false);
    expect(isSafeCheckoutUrl("javascript:alert(1)")).toBe(false);
    expect(isSafeCheckoutUrl("data:text/html,<script>")).toBe(false);
    expect(isSafeCheckoutUrl("not a url")).toBe(false);
    expect(isSafeCheckoutUrl(null)).toBe(false);
  });
});

describe("report helpers", () => {
  it("formats cells by type", () => {
    expect(formatCell(true, { type: "bool" })).toBe("Yes");
    expect(formatCell(false, { type: "bool" })).toBe("No");
    expect(formatCell(1234.5, { type: "number" })).toBe("1,234.5");
    expect(formatCell("2026-11-16T10:30:00+00:00", { type: "datetime" })).toBe("2026-11-16 10:30 UTC");
    expect(formatCell("not a date", { type: "datetime" })).toBe("not a date");
    expect(formatCell(null, { type: "string" })).toBe("—");
    expect(formatCell("=cmd", { type: "string" })).toBe("=cmd");
  });

  it("formats summary metrics", () => {
    expect(formatMetric(2)).toBe("2");
    expect(formatMetric(98.456)).toBe("98.46");
    expect(formatMetric("1.2.0")).toBe("1.2.0");
    expect(formatMetric(null)).toBe("—");
  });
});

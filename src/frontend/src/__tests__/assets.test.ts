import { describe, expect, it } from "vitest";
import {
  DEFAULT_FILTERS,
  activeFilterCount,
  assigneeLabel,
  auditActionLabel,
  categoryLabel,
  formatDay,
  formatMoney,
  linkLabel,
  parseRegisterQuery,
  registerQuery,
  statusTone,
  warrantyTone,
} from "@/lib/assets";
import { activeHref } from "@/components/shell/nav-config";
import { emptyAssetForm, toAssetInput } from "@/components/features/assets/asset-form";

describe("asset register query", () => {
  it("omits defaults and round-trips through the URL", () => {
    expect(registerQuery(DEFAULT_FILTERS).toString()).toBe("");
    const filters = { ...DEFAULT_FILTERS, search: "dell", category: "Laptop" as const, warranty: "ExpiringSoon" as const, sort: "PurchaseCost" as const, direction: "Desc" as const, pageSize: 50 };
    const qs = registerQuery(filters, 3);
    expect(qs.get("category")).toBe("Laptop");
    expect(qs.get("page")).toBe("3");
    expect(qs.has("status")).toBe(false);
    const parsed = parseRegisterQuery(Object.fromEntries(qs));
    expect(parsed).toEqual({ ...filters, page: 3 });
  });

  it("rejects malformed URL values", () => {
    const parsed = parseRegisterQuery({ category: "Spaceship", status: "InUse", branchId: "'; drop table", pageSize: "1000", page: "-2", sort: "Password", direction: "sideways", assigned: "maybe" });
    expect(parsed.category).toBe("");
    expect(parsed.status).toBe("InUse");
    expect(parsed.branchId).toBe("");
    expect(parsed.pageSize).toBe(25);
    expect(parsed.page).toBe(1);
    expect(parsed.sort).toBe("AssetTag");
    expect(parsed.direction).toBe("Asc");
    expect(parsed.assigned).toBe("");
  });

  it("counts active filters, not search or sorting", () => {
    expect(activeFilterCount({ ...DEFAULT_FILTERS, search: "x", sort: "Name" })).toBe(0);
    expect(activeFilterCount({ ...DEFAULT_FILTERS, status: "Missing", endpointLink: "Suggested" })).toBe(2);
  });
});

describe("asset formatting", () => {
  it("formats date-only values without time-zone shifts", () => {
    expect(formatDay("2026-01-01")).toContain("2026");
    expect(formatDay("2026-01-01")).toMatch(/1/);
    expect(formatDay(null)).toBe("—");
    expect(formatDay("not a date")).toBe("—");
  });

  it("formats money with and without a currency", () => {
    expect(formatMoney(1234.5, "USD")).toMatch(/1,234\.50/);
    expect(formatMoney(10, null)).toMatch(/10\.00/);
    expect(formatMoney(null, "USD")).toBe("—");
  });

  it("labels categories, statuses, links and audit actions", () => {
    expect(categoryLabel("ZebraHandheld")).toBe("Zebra handheld");
    expect(statusTone("Missing")).toBe("danger");
    expect(statusTone("InUse")).toBe("success");
    expect(warrantyTone("ExpiringSoon")).toBe("warning");
    expect(linkLabel("Suggested")).toBe("Review match");
    expect(auditActionLabel("endpoint_match.auto_approved")).toBe("Endpoint linked automatically");
    expect(auditActionLabel("custom.thing")).toBe("custom.thing");
    expect(assigneeLabel({ userId: null, displayName: "Nadia", email: null, employeeNumber: "E-9", isMember: false })).toBe("Nadia (E-9)");
    expect(assigneeLabel({ userId: "u", displayName: null, email: null, employeeNumber: null, isMember: false })).toBe("Former member");
  });
});

describe("asset form", () => {
  it("converts form values and validates numbers", () => {
    const values = { ...emptyAssetForm("usd"), name: " ThinkPad ", serialNumber: "  ", purchaseCost: "1299.99", usefulLifeMonths: "36", depreciationMethod: "StraightLine" as const };
    const { input, errors } = toAssetInput(values);
    expect(errors).toEqual({});
    expect(input.name).toBe("ThinkPad");
    expect(input.serialNumber).toBeNull();
    expect(input.purchaseCost).toBe(1299.99);
    expect(input.currency).toBe("USD");
    expect(input.usefulLifeMonths).toBe(36);

    const bad = toAssetInput({ ...values, purchaseCost: "abc", usefulLifeMonths: "3.5", name: "" });
    expect(Object.keys(bad.errors).sort()).toEqual(["name", "purchaseCost", "usefulLifeMonths"]);
  });
});

describe("navigation", () => {
  it("highlights the most specific matching item", () => {
    expect(activeHref("/assets")).toBe("/assets");
    expect(activeHref("/assets/0190-uuid")).toBe("/assets");
    expect(activeHref("/assets/matching")).toBe("/assets/matching");
    expect(activeHref("/assets/audits/abc")).toBe("/assets/audits");
    expect(activeHref("/my-assets")).toBe("/my-assets");
    expect(activeHref("/nowhere")).toBeNull();
  });
});

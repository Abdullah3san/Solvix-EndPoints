import { describe, expect, it } from "vitest";
import type { DeviceStatsDto } from "@/lib/api/types";
import { buildOverview } from "@/lib/dashboard/model";
import { demoDashboard } from "@/lib/dev/demo-data";
import { filtersFromSearchParams, toApiQuery, activeFilterCount, emptyDeviceFilters } from "@/lib/device-query";
import { isDemoDataEnabled } from "@/lib/env";
import { matchPages } from "@/components/shell/global-search";
import { navigation, visibleNavigation } from "@/components/shell/nav-config";

const stats: DeviceStatsDto = {
  total: 12,
  active: 10,
  online: 7,
  offline: 3,
  retired: 2,
  updateAvailable: 4,
  requiringAttention: 1,
  latestAgentVersion: "1.2.0",
  agentVersions: [],
};
const now = new Date("2026-11-16T12:00:00Z");

describe("dashboard overview", () => {
  it("uses API figures and marks modules without a backend as unavailable", () => {
    const overview = buildOverview({ stats: { ok: true, value: stats }, demo: null, now });
    expect(overview.demo).toBe(false);
    expect(overview.metrics.managedDevices).toEqual({ status: "ok", value: 10 });
    expect(overview.metrics.onlineDevices).toEqual({ status: "ok", value: 7 });
    expect(overview.metrics.offlineDevices).toEqual({ status: "ok", value: 3 });
    expect(overview.metrics.attentionDevices).toEqual({ status: "ok", value: 1 });
    for (const key of ["mobileDevices", "itAssets", "criticalAlerts", "pendingDeployments"] as const) {
      expect(overview.metrics[key].status).toBe("unavailable");
    }
    expect(overview.activity.status).toBe("unavailable");
  });

  it("reports errors instead of zeros when the API fails", () => {
    const overview = buildOverview({ stats: { ok: false, message: "Service unavailable" }, demo: null, now });
    expect(overview.metrics.managedDevices).toEqual({ status: "error", message: "Service unavailable" });
    expect(overview.fleet).toBeNull();
    expect(overview.fleetError).toBe("Service unavailable");
  });

  it("treats a figure the API does not send as unavailable, not zero", () => {
    const older = { ...stats, requiringAttention: undefined } as unknown as DeviceStatsDto;
    expect(buildOverview({ stats: { ok: true, value: older }, demo: null, now }).metrics.attentionDevices.status).toBe("unavailable");
  });

  it("flags every demo value and never replaces real device figures", () => {
    const overview = buildOverview({ stats: { ok: true, value: stats }, demo: demoDashboard, now });
    expect(overview.demo).toBe(true);
    expect(overview.metrics.itAssets).toEqual({ status: "ok", value: demoDashboard.itAssets, demo: true });
    expect(overview.metrics.managedDevices).toEqual({ status: "ok", value: 10 });
    expect(overview.activity).toMatchObject({ status: "ok", demo: true });
  });
});

describe("demo data gate", () => {
  it("is on only in development with the explicit flag", () => {
    expect(isDemoDataEnabled({ NODE_ENV: "development", NEXT_PUBLIC_SOLVIX_DEMO_DATA: "true" })).toBe(true);
    expect(isDemoDataEnabled({ NODE_ENV: "development" })).toBe(false);
    expect(isDemoDataEnabled({ NODE_ENV: "production", NEXT_PUBLIC_SOLVIX_DEMO_DATA: "true" })).toBe(false);
    expect(isDemoDataEnabled({ NODE_ENV: "test", NEXT_PUBLIC_SOLVIX_DEMO_DATA: "true" })).toBe(false);
  });
});

describe("device list query", () => {
  it("maps the status facet to the API's status and connectivity parameters", () => {
    const base = { sortBy: "Name" as const, sortDirection: "Asc" as const, page: 2, pageSize: 50 };
    expect(toApiQuery({ ...base, filters: { ...emptyDeviceFilters, state: "Offline" } })).toMatchObject({ connectivity: "Offline", status: undefined, page: 2, pageSize: 50 });
    expect(toApiQuery({ ...base, filters: { ...emptyDeviceFilters, state: "Retired", attention: true, search: "  lap " } })).toMatchObject({
      status: "Retired",
      connectivity: undefined,
      attention: true,
      search: "lap",
    });
  });

  it("validates deep-link parameters", () => {
    const id = "01a1d173-5268-75f6-ac1c-813ec3f338ef";
    expect(filtersFromSearchParams({ connectivity: "Online", groupId: id, attention: "true", search: "fin" })).toEqual({
      ...emptyDeviceFilters,
      state: "Online",
      groupId: id,
      attention: true,
      search: "fin",
    });
    const hostile = filtersFromSearchParams({ connectivity: "Sideways", groupId: "1 OR 1=1", branchId: ["x", id], agentVersion: "<script>" });
    expect(hostile).toEqual(emptyDeviceFilters);
    expect(activeFilterCount(hostile)).toBe(0);
  });
});

describe("navigation", () => {
  const all = navigation.flatMap((s) => s.items);

  it("hides admin and development entries from other audiences", () => {
    const member = visibleNavigation({ isAdmin: false, isDevelopment: false }).flatMap((s) => s.items.map((i) => i.label));
    expect(member).toContain("Devices");
    expect(member).not.toContain("Enrollment");
    expect(member).not.toContain("Design system");
    expect(visibleNavigation({ isAdmin: true, isDevelopment: true, isPlatformAdmin: true }).flatMap((s) => s.items)).toHaveLength(all.length);
  });

  it("shows the provider console only to the platform operator", () => {
    const labels = (options: { isAdmin: boolean; isPlatformAdmin?: boolean }) =>
      visibleNavigation({ ...options, isDevelopment: false }).flatMap((s) => s.items.map((i) => i.label));
    expect(labels({ isAdmin: true })).not.toContain("Companies");
    expect(labels({ isAdmin: true, isPlatformAdmin: false })).not.toContain("Companies");
    expect(labels({ isAdmin: false, isPlatformAdmin: true })).toContain("Companies");
    expect(visibleNavigation({ isAdmin: true, isDevelopment: false }).some((s) => s.title === "Platform")).toBe(false);
  });

  it("matches pages by label and keyword in the global search", () => {
    expect(matchPages(all, "branch").map((i) => i.label)).toEqual(["Branches"]);
    expect(matchPages(all, "enroll").map((i) => i.label)).toEqual(["Enrollment"]);
    expect(matchPages(all, "")).toHaveLength(all.length);
  });
});

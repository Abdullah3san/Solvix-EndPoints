import { describe, expect, it } from "vitest";
import type { MonitoringSummaryDto, MonitoringUpdateDto } from "@/lib/api/types";
import { applyUpdate, eventTone, formatDuration, formatPercent, networkTone, osTone, stateTone } from "@/lib/monitoring";
import { isAllowedRealtimePath } from "@/lib/realtime";
import { validateThresholds } from "@/components/features/monitoring/monitoring-settings-form";

const summary: MonitoringSummaryDto = {
  generatedAtUtc: "2026-11-12T10:00:00Z",
  devices: { total: 3, online: 3, warning: 0, offline: 0 },
  retiredDevices: 0,
  availability7dPercent: 100,
  availability30dPercent: 100,
  branches: [{ branchId: "b1", name: "HQ", code: "HQ", devices: { total: 3, online: 3, warning: 0, offline: 0 }, availability7dPercent: 100 }],
  operatingSystems: [],
  signals: { network: { Unknown: 0, Reachable: 3, Degraded: 0, Unreachable: 0 }, operatingSystem: { Unknown: 0, Healthy: 3, Warning: 0, Critical: 0 } },
  recentEvents: [],
  openAlerts: 0,
  settings: { heartbeatIntervalSeconds: 60, warningAfterSeconds: 120, offlineAfterSeconds: 300, offlineAlertsEnabled: true, recoveryNotificationsEnabled: true, isDefault: true },
};

const event = {
  id: "e1",
  deviceId: "d1",
  deviceName: "PC-1",
  type: "WentOffline" as const,
  signal: "Agent" as const,
  occurredAtUtc: "2026-11-12T10:05:00Z",
  fromStatus: "Warning",
  toStatus: "Offline",
  detail: "No contact",
  lastSeenAtUtc: "2026-11-12T10:00:00Z",
  durationSeconds: 300,
  failedAttempts: null,
};

describe("applyUpdate", () => {
  it("moves counters, prepends events once and counts alerts", () => {
    const update: MonitoringUpdateDto = {
      statusChanges: [{ deviceId: "d1", deviceName: "PC-1", branchId: "b1", from: "Warning", to: "Offline", changedAtUtc: event.occurredAtUtc, lastSeenAtUtc: event.lastSeenAtUtc }],
      events: [event],
      alerts: [
        {
          id: "a1",
          deviceId: "d1",
          deviceName: "PC-1",
          type: "DeviceOffline",
          status: "Open",
          message: "offline",
          openedAtUtc: event.occurredAtUtc,
          lastSeenAtUtc: event.lastSeenAtUtc,
          resolvedAtUtc: null,
          downtimeSeconds: null,
          acknowledgedAtUtc: null,
        },
      ],
    };
    const start = { ...summary, devices: { total: 3, online: 2, warning: 1, offline: 0 } };
    const next = applyUpdate(start, update);
    expect(next.devices).toEqual({ total: 3, online: 2, warning: 0, offline: 1 });
    expect(next.branches[0].devices.offline).toBe(1);
    expect(next.recentEvents).toHaveLength(1);
    expect(next.openAlerts).toBe(1);
    // Replayed update (e.g. after reconnect) does not duplicate the event.
    expect(applyUpdate(next, { ...update, statusChanges: [], alerts: [] }).recentEvents).toHaveLength(1);
  });

  it("decrements open alerts on recovery", () => {
    const next = applyUpdate({ ...summary, openAlerts: 1 }, {
      statusChanges: [],
      events: [],
      alerts: [
        {
          id: "a1",
          deviceId: "d1",
          deviceName: "PC-1",
          type: "DeviceOffline",
          status: "Resolved",
          message: "offline",
          openedAtUtc: "2026-11-12T10:05:00Z",
          lastSeenAtUtc: "2026-11-12T10:00:00Z",
          resolvedAtUtc: "2026-11-12T10:10:00Z",
          downtimeSeconds: 600,
          acknowledgedAtUtc: null,
        },
      ],
    });
    expect(next.openAlerts).toBe(0);
  });
});

describe("formatting and tones", () => {
  it("formats durations and percentages", () => {
    expect(formatDuration(45)).toBe("45 s");
    expect(formatDuration(300)).toBe("5 min");
    expect(formatDuration(3720)).toBe("1 h 2 min");
    expect(formatDuration(266400)).toBe("3 d 2 h");
    expect(formatDuration(null)).toBe("—");
    expect(formatPercent(99.999)).toBe("100%");
    expect(formatPercent(97.5)).toBe("97.50%");
    expect(formatPercent(null)).toBe("—");
  });

  it("keeps the three signals distinct and greys out stale readings", () => {
    expect(stateTone("Warning")).toBe("warning");
    expect(stateTone("Offline")).toBe("danger");
    expect(networkTone("Degraded")).toBe("warning");
    expect(networkTone("Reachable", true)).toBe("neutral");
    expect(osTone("Critical")).toBe("danger");
    expect(eventTone(event)).toBe("danger");
  });
});

describe("validateThresholds", () => {
  it("mirrors the server rules", () => {
    expect(validateThresholds(60, 120, 300)).toEqual({});
    expect(validateThresholds(60, 90, 300)).toHaveProperty("warningAfterSeconds");
    expect(validateThresholds(60, 120, 120)).toHaveProperty("offlineAfterSeconds");
    expect(validateThresholds(5, 120, 300)).toHaveProperty("heartbeatIntervalSeconds");
  });
});

describe("realtime BFF allow-list", () => {
  it("only forwards to the monitoring hub", () => {
    expect(isAllowedRealtimePath(["hubs", "monitoring"])).toBe(true);
    expect(isAllowedRealtimePath(["hubs", "monitoring", "negotiate"])).toBe(true);
    expect(isAllowedRealtimePath(["hubs", "other"])).toBe(false);
    expect(isAllowedRealtimePath(["api", "v1", "devices"])).toBe(false);
    expect(isAllowedRealtimePath(["hubs", "monitoring", "..", "x"])).toBe(false);
    expect(isAllowedRealtimePath(undefined)).toBe(false);
  });
});

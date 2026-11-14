import { describe, expect, it } from "vitest";
import { connectivityTone, deviceLabel, formatBytes, formatRelative, healthTone, tokenStateTone } from "@/lib/devices";
import { filenameFromContentDisposition } from "@/lib/api/client";

describe("device helpers", () => {
  it("formats sizes in binary units", () => {
    expect(formatBytes(0)).toBe("0 B");
    expect(formatBytes(1536)).toBe("1.5 KB");
    expect(formatBytes(16 * 1024 ** 3)).toBe("16 GB");
    expect(formatBytes(512 * 1000 ** 3)).toBe("477 GB");
    expect(formatBytes(null)).toBe("—");
    expect(formatBytes(-1)).toBe("—");
  });

  it("formats last-seen times relative to now", () => {
    const now = new Date("2026-11-12T12:00:00Z");
    expect(formatRelative("2026-11-12T11:59:50Z", now)).toBe("just now");
    expect(formatRelative("2026-11-12T11:55:00Z", now)).toBe("5 min ago");
    expect(formatRelative("2026-11-12T09:00:00Z", now)).toBe("3 h ago");
    expect(formatRelative("2026-11-09T12:00:00Z", now)).toBe("3 d ago");
    expect(formatRelative(null, now)).toBe("—");
    expect(formatRelative("garbage", now)).toBe("—");
  });

  it("maps statuses to badge tones", () => {
    expect(connectivityTone("Active", "Online")).toBe("success");
    expect(connectivityTone("Active", "Offline")).toBe("warning");
    expect(connectivityTone("Retired", "Online")).toBe("neutral");
    expect(healthTone("Unhealthy")).toBe("danger");
    expect(healthTone(null)).toBe("neutral");
    expect(tokenStateTone("Active")).toBe("success");
    expect(tokenStateTone("Revoked")).toBe("danger");
    expect(tokenStateTone("Expired")).toBe("neutral");
  });

  it("prefers the display name", () => {
    expect(deviceLabel({ displayName: "Reception PC", hostname: "DESKTOP-1" })).toBe("Reception PC");
    expect(deviceLabel({ displayName: "  ", hostname: "DESKTOP-1" })).toBe("DESKTOP-1");
  });

  it("reads download file names from Content-Disposition safely", () => {
    const fallback = "agent.zip";
    expect(filenameFromContentDisposition(`attachment; filename=SolvixAgent-acme.zip; filename*=UTF-8''SolvixAgent-acme.zip`, fallback)).toBe("SolvixAgent-acme.zip");
    expect(filenameFromContentDisposition('attachment; filename="SolvixAgent-berlin-tills.zip"', fallback)).toBe("SolvixAgent-berlin-tills.zip");
    expect(filenameFromContentDisposition(null, fallback)).toBe(fallback);
    expect(filenameFromContentDisposition("inline", fallback)).toBe(fallback);
    expect(filenameFromContentDisposition("attachment; filename*=UTF-8''..%2F..%2Fevil.exe", fallback)).toBe(".._.._evil.exe");
  });
});

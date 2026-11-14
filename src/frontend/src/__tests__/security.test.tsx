import { describe, expect, it } from "vitest";
import { render, screen } from "@testing-library/react";
import { bandLabel, bandTone, humanize, outcomeLabel, severityTone, timeAgo, productScopeShort } from "@/lib/security";
import { isActivePath } from "@/components/shell/nav-config";
import { ScopeNotice, ScoreBadge, SeverityBadge } from "@/components/features/security/security-ui";

describe("security presentation helpers", () => {
  it("maps severity to a consistent tone", () => {
    expect(severityTone("Critical")).toBe("danger");
    expect(severityTone("High")).toBe("danger");
    expect(severityTone("Medium")).toBe("warning");
    expect(severityTone("Low")).toBe("brand");
    expect(severityTone("Info")).toBe("neutral");
  });

  it("never presents missing data as compliant", () => {
    expect(bandLabel("Unknown")).toBe("No recent data");
    expect(bandTone("Unknown")).toBe("neutral");
    expect(bandTone("Compliant")).toBe("success");
    expect(outcomeLabel("Unknown")).toBe("Could not be checked");
  });

  it("humanizes enum names and relative times", () => {
    expect(humanize("PendingApproval")).toBe("Pending approval");
    const now = Date.parse("2026-01-02T00:00:00Z");
    expect(timeAgo(null, now)).toBe("never");
    expect(timeAgo("2026-01-01T23:30:00Z", now)).toBe("30 min ago");
    expect(timeAgo("2025-12-29T00:00:00Z", now)).toBe("4 days ago");
  });

  it("keeps the Overview nav item from matching every security page", () => {
    expect(isActivePath("/security", "/security")).toBe(true);
    expect(isActivePath("/security/devices", "/security")).toBe(false);
    expect(isActivePath("/security/devices/123", "/security/devices")).toBe(true);
  });
});

describe("security components", () => {
  it("states that the product is not antivirus or EDR", () => {
    render(<ScopeNotice />);
    expect(screen.getByText(/not antivirus or EDR/i)).toBeTruthy();
    expect(productScopeShort).toMatch(/configuration, not whether a device is free of compromise/);
  });

  it("shows a dash instead of a misleading zero when there is no score", () => {
    render(<ScoreBadge score={null} band="Unknown" />);
    expect(screen.getByText("—")).toBeTruthy();
    expect(screen.getByText("No recent data")).toBeTruthy();
  });

  it("renders severity text, not colour alone", () => {
    render(<SeverityBadge severity="High" />);
    expect(screen.getByText("High")).toBeTruthy();
  });
});

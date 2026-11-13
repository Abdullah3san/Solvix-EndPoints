import { describe, expect, it } from "vitest";
import { render, screen } from "@testing-library/react";
import { navigation } from "@/components/shell/nav-config";
import { DeploymentStatusBadge, TargetStatusBadge, formatBytes, humanize, sensitivityLabels } from "@/components/features/software/labels";

describe("software management UI helpers", () => {
  it("lists the software pages behind the software.view permission", () => {
    const section = navigation.find((s) => s.title === "Software");
    expect(section?.items.map((i) => i.href)).toEqual([
      "/software/deployments",
      "/software/packages",
      "/software/inventory",
      "/software/policies",
    ]);
    expect(section?.items.every((i) => i.permission === "software.view")).toBe(true);
  });

  it("renders readable statuses", () => {
    render(
      <>
        <DeploymentStatusBadge status="CompletedWithErrors" />
        <TargetStatusBadge status="TimedOut" />
      </>,
    );
    expect(screen.getByText("Completed With Errors")).toBeTruthy();
    expect(screen.getByText("Timed Out")).toBeTruthy();
    expect(humanize("installer_failed")).toBe("installer failed");
  });

  it("formats sizes and explains every sensitivity reason the API can return", () => {
    expect(formatBytes(512)).toBe("512 B");
    expect(formatBytes(5 * 1024 * 1024)).toBe("5.0 MB");
    for (const reason of ["unsigned_package", "untrusted_publisher", "package_marked_sensitive", "uninstall", "large_deployment", "server_targets"]) {
      expect(sensitivityLabels[reason]).toBeTruthy();
    }
  });
});

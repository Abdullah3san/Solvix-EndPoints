import { describe, expect, it, vi } from "vitest";
import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { deploymentActions, versionActions } from "@/lib/api/endpoint-types";
import { ConfirmDialog, describeTarget, type TargetOptions } from "@/components/features/endpoints/common";
import { navigation } from "@/components/shell/nav-config";

const options: TargetOptions = {
  branches: [{ id: "b1", name: "Berlin" }],
  departments: [{ id: "d1", name: "Berlin / IT" }],
  groups: [{ id: "g1", name: "Pilot ring" }],
  devices: [{ id: "x1", hostname: "LAPTOP-01" }],
};

describe("deployment actions", () => {
  it("only offers actions that are valid for the state", () => {
    expect(deploymentActions("Draft", "NotStarted")).toEqual(["schedule"]);
    expect(deploymentActions("InProgress", "Pilot")).toContain("promote");
    expect(deploymentActions("InProgress", "Broad")).not.toContain("promote");
    expect(deploymentActions("Halted", "Pilot")).toEqual(["force-promote", "cancel"]);
    expect(deploymentActions("Halted", "Broad")).toEqual(["resume", "cancel"]);
    expect(deploymentActions("Completed", "Broad")).toEqual([]);
  });

  it("follows the policy version workflow", () => {
    expect(versionActions("Draft")).toEqual(["submit"]);
    expect(versionActions("PendingApproval")).toEqual(["approve", "reject"]);
    expect(versionActions("Superseded")).toContain("publish");
    expect(versionActions("Published")).toEqual(["restore"]);
  });
});

describe("targets", () => {
  it("describes inclusions and exclusions by name", () => {
    expect(describeTarget({ targetType: "AllDevices", targetId: null, isExclusion: false }, options)).toBe("All devices");
    expect(describeTarget({ targetType: "DeviceGroup", targetId: "g1", isExclusion: false }, options)).toBe("Pilot ring");
    expect(describeTarget({ targetType: "Device", targetId: "x1", isExclusion: true }, options)).toBe("Except LAPTOP-01");
  });
});

describe("confirm dialog", () => {
  it("requires a reason and acknowledgement before confirming a destructive approval", async () => {
    const onConfirm = vi.fn();
    render(
      <ConfirmDialog
        open
        title="Approve v2"
        confirmLabel="Approve"
        requireReason
        acknowledgement="I authorise these destructive changes."
        destructive
        onClose={() => {}}
        onConfirm={onConfirm}
      />,
    );
    const confirm = screen.getByRole("button", { name: "Approve" });
    expect(confirm).toBeDisabled();
    await userEvent.type(screen.getByLabelText(/reason/i), "CAB-42");
    expect(confirm).toBeDisabled();
    await userEvent.click(screen.getByRole("checkbox"));
    expect(confirm).toBeEnabled();
    await userEvent.click(confirm);
    expect(onConfirm).toHaveBeenCalledWith("CAB-42", true);
  });
});

describe("navigation", () => {
  it("exposes the endpoint management pages and an admin-only audit log", () => {
    const endpoints = navigation.find((s) => s.title === "Endpoints");
    expect(endpoints?.items.map((i) => i.href)).toEqual([
      "/endpoints/devices",
      "/endpoints/patches",
      "/endpoints/deployments",
      "/endpoints/maintenance-windows",
      "/endpoints/configuration",
    ]);
    const audit = navigation.flatMap((s) => s.items).find((i) => i.href === "/settings/audit");
    expect(audit?.adminOnly).toBe(true);
  });
});

import { afterEach, describe, expect, it, vi } from "vitest";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { PendingApprovalsPanel } from "@/components/features/devices/pending-approvals-panel";
import { DeploymentProfilesPanel } from "@/components/features/devices/deployment-profiles-panel";
import { DownloadAgentButton } from "@/components/shell/download-agent-button";
import { SessionProvider, type Session } from "@/components/providers/session-provider";
import { ToastProvider } from "@/components/providers/toast-provider";
import { installerUrl, type DeploymentProfileDto, type Permission, type PendingEnrollmentDto } from "@/lib/api/types";

const post = vi.fn();
const get = vi.fn();
vi.mock("@/lib/api/client", () => ({
  api: { post: (...a: unknown[]) => post(...a), get: (...a: unknown[]) => get(...a), put: vi.fn(), delete: vi.fn() },
}));
vi.mock("next/link", () => ({ default: ({ children, href }: { children: React.ReactNode; href: string }) => <a href={href}>{children}</a> }));

function session(permissions: Permission[]): Session {
  return {
    user: { id: "u1", email: "owner@acme.test", displayName: "Owner" },
    tenant: { id: "t1", slug: "acme", name: "Acme Corp" },
    sessionId: "s1",
    roles: ["Custom"],
    permissions,
    mfaEnabled: false,
    mfaVerified: false,
    mfaEnrollmentRequired: false,
  };
}

const profile = (over: Partial<DeploymentProfileDto> = {}): DeploymentProfileDto => ({
  id: "p1",
  name: "Berlin tills",
  description: null,
  branchId: "b1",
  branchName: "Berlin",
  departmentId: null,
  departmentName: null,
  deviceGroupId: null,
  deviceGroupName: null,
  isEnabled: true,
  codeVersion: 1,
  pendingCount: 2,
  enrolledCount: 5,
  lastRequestAtUtc: null,
  createdAtUtc: "2026-11-01T00:00:00Z",
  ...over,
});

const request = (over: Partial<PendingEnrollmentDto> = {}): PendingEnrollmentDto => ({
  id: "r1",
  profileId: "p1",
  profileName: "Berlin tills",
  status: "Pending",
  hostname: "TILL-03",
  manufacturer: "Contoso",
  model: "Book 15",
  serialNumber: "SN123",
  osName: "Windows 11 Pro",
  osVersion: "10.0.22631",
  agentVersion: "1.0.0",
  sourceAddress: "10.0.0.7",
  requestedAtUtc: "2026-11-17T10:00:00Z",
  expiresAtUtc: "2026-12-01T10:00:00Z",
  isExpired: false,
  branchName: "Berlin",
  departmentName: null,
  deviceGroupName: null,
  existingDeviceId: null,
  existingDeviceHostname: null,
  deviceId: null,
  decidedAtUtc: null,
  decisionNote: null,
  ...over,
});

const wrap = (ui: React.ReactNode, permissions: Permission[] = ["devices.enroll"]) =>
  render(
    <SessionProvider session={session(permissions)}>
      <ToastProvider>{ui}</ToastProvider>
    </SessionProvider>,
  );

afterEach(() => vi.clearAllMocks());

describe("download agent (top bar)", () => {
  it("is hidden without devices.enroll", () => {
    wrap(<DownloadAgentButton />, ["devices.view"]);
    expect(screen.queryByRole("button", { name: /download agent/i })).not.toBeInTheDocument();
  });

  it("offers one .exe link per enabled profile", async () => {
    get.mockResolvedValue([profile(), profile({ id: "p2", name: "Old", isEnabled: false })]);
    wrap(<DownloadAgentButton />);
    await userEvent.click(screen.getByRole("button", { name: /download agent/i }));
    const link = await screen.findByRole("link", { name: /download \.exe/i });
    expect(link).toHaveAttribute("href", installerUrl("p1"));
    expect(link).toHaveAttribute("download");
    expect(screen.getAllByRole("link", { name: /download \.exe/i })).toHaveLength(1); // disabled profiles are not offered
    expect(screen.getByText("2 waiting")).toBeInTheDocument();
  });
});

describe("deployment profiles", () => {
  it("shows the installer download only for enabled profiles", () => {
    wrap(
      <DeploymentProfilesPanel
        profiles={[profile(), profile({ id: "p2", name: "Disabled one", isEnabled: false })]}
        groups={[]}
        branches={[]}
        departments={[]}
        onChanged={async () => {}}
      />,
    );
    expect(screen.getAllByRole("link", { name: /download \.exe/i })).toHaveLength(1);
    expect(screen.getAllByText("Berlin").length).toBeGreaterThan(0);
  });
});

describe("pending approvals", () => {
  it("approves a single computer", async () => {
    post.mockResolvedValue({});
    const onChanged = vi.fn().mockResolvedValue(undefined);
    wrap(<PendingApprovalsPanel items={[request()]} onChanged={onChanged} />);
    expect(screen.getByText("TILL-03")).toBeInTheDocument();
    expect(screen.getByText("S/N SN123")).toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: "Approve TILL-03" }));
    await waitFor(() => expect(post).toHaveBeenCalledWith("pending-enrollments/r1/approve", {}));
    expect(onChanged).toHaveBeenCalled();
  });

  it("approves several selected computers at once and reports failures", async () => {
    post.mockResolvedValue({ succeeded: 1, failed: [{ id: "r2", code: "enrollment_request_expired", message: "expired" }] });
    wrap(<PendingApprovalsPanel items={[request(), request({ id: "r2", hostname: "TILL-04" })]} onChanged={async () => {}} />);
    await userEvent.click(screen.getByRole("checkbox", { name: "Select all" }));
    await userEvent.click(screen.getByRole("button", { name: /approve selected \(2\)/i }));
    await waitFor(() => expect(post).toHaveBeenCalledWith("pending-enrollments/bulk/approve", { ids: ["r1", "r2"] }));
    expect(await screen.findByText("1 approved, 1 failed")).toBeInTheDocument();
  });

  it("flags a computer that is already known and hides decided or expired requests", () => {
    wrap(
      <PendingApprovalsPanel
        items={[
          request({ existingDeviceId: "d9", existingDeviceHostname: "TILL-03" }),
          request({ id: "r3", hostname: "OLD-PC", isExpired: true }),
          request({ id: "r4", hostname: "DONE-PC", status: "Approved" }),
        ]}
        onChanged={async () => {}}
      />,
    );
    expect(screen.getByText("Known computer")).toBeInTheDocument();
    expect(screen.queryByText("OLD-PC")).not.toBeInTheDocument();
    expect(screen.queryByText("DONE-PC")).not.toBeInTheDocument();
  });

  it("shows an empty state", () => {
    wrap(<PendingApprovalsPanel items={[]} onChanged={async () => {}} />);
    expect(screen.getByText(/no computers are waiting/i)).toBeInTheDocument();
  });
});

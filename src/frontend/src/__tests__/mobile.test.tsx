import { afterEach, describe, expect, it, vi } from "vitest";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import * as React from "react";
import { ToastProvider } from "@/components/providers/toast-provider";
import { SessionProvider } from "@/components/providers/session-provider";
import { ApprovalsView } from "@/components/features/mobile/approvals-view";
import { CommandDialog } from "@/components/features/mobile/command-dialog";
import { DevicesView } from "@/components/features/mobile/devices-view";
import { PolicySettingsForm } from "@/components/features/mobile/policy-form";
import { Sidebar } from "@/components/shell/sidebar";
import { crumbsFromPathname } from "@/components/shell/breadcrumbs";
import { emptySettings, intOrNull, scenarioKey, scenarios } from "@/lib/mdm";
import type { CapabilitiesDto, CommandDto, ComplianceSummaryDto, DeviceDto } from "@/lib/api/mdm-types";

vi.mock("next/navigation", () => ({ usePathname: () => "/mobile/devices", useRouter: () => ({ refresh: () => {} }) }));

const session = (role: "Owner" | "Admin" | "Member" = "Admin") => ({
  user: { id: "me", email: "me@acme.test", displayName: "Me" },
  tenant: { id: "t1", slug: "acme", name: "Acme", role },
});

function wrap(ui: React.ReactNode, role: "Owner" | "Admin" | "Member" = "Admin") {
  return render(
    <SessionProvider session={session(role)}>
      <ToastProvider>{ui}</ToastProvider>
    </SessionProvider>,
  );
}

const device = (over: Partial<DeviceDto> = {}): DeviceDto => ({
  id: "d1", platform: "Android", mode: "FullyManaged", ownership: "CompanyOwned", state: "Enrolled", displayName: "Pixel 8", manufacturer: "Google",
  model: "Pixel 8", serialNumber: "SER1", osVersion: "14", securityPatchLevel: null, isEncrypted: true, hasPasscode: true, isSupervised: null,
  integrity: "Intact", policyId: null, appliedPolicyVersion: null, compliance: "Compliant", complianceEvaluatedAtUtc: null, enrolledAtUtc: null,
  lastSeenAtUtc: null, retiredAtUtc: null, createdAtUtc: "2026-11-13T00:00:00Z", ...over,
});

const caps = (over: Partial<CapabilitiesDto> = {}): CapabilitiesDto => ({
  platform: "Android", mode: "FullyManaged", ownership: "CompanyOwned",
  commands: ["SyncInventory", "ApplyPolicy", "Lock", "ResetPasscode", "Reboot", "Retire", "Wipe"], commandsRequiringApproval: ["Retire", "Wipe"],
  canRemoveCorporateDataOnly: false, canFactoryReset: true, canReadDeviceIdentifiers: true, canEnforceDeviceWidePolicy: true, limitations: [], ...over,
});

const summary: ComplianceSummaryDto = {
  total: 3, compliant: 2, nonCompliant: 1, unknown: 0, byPlatform: [], byOwnership: [], topViolations: [], pendingApprovals: 2, generatedAtUtc: "2026-11-13T00:00:00Z",
};

afterEach(() => vi.restoreAllMocks());

function mockFetch(handler: (url: string, init?: RequestInit) => unknown) {
  return vi.spyOn(globalThis, "fetch").mockImplementation(async (input, init) => {
    const body = handler(String(input), init);
    return new Response(JSON.stringify(body), { status: 200, headers: { "content-type": "application/json" } });
  });
}

describe("mobile helpers", () => {
  it("lists exactly the seven valid management scenarios, none personal-full-enrollment", () => {
    expect(scenarios).toHaveLength(7);
    expect(new Set(scenarios.map(scenarioKey)).size).toBe(7);
    expect(scenarios.some((s) => s.platform === "Ios" && s.mode === "DeviceEnrollment" && s.ownership === "PersonallyOwned")).toBe(false);
    expect(scenarios.some((s) => s.mode === "FullyManaged" && s.ownership === "PersonallyOwned")).toBe(false);
  });

  it("parses optional integers", () => {
    expect(intOrNull("12")).toBe(12);
    expect(intOrNull("")).toBeNull();
    expect(intOrNull("abc")).toBeNull();
  });

  it("labels uuid breadcrumb segments instead of showing the raw id", () => {
    expect(crumbsFromPathname("/mobile/devices/0197a1d4-bf2d-7ae8-820e-6201c6404d10")).toEqual([
      { label: "Mobile devices", href: "/mobile" },
      { label: "Devices", href: "/mobile/devices" },
      { label: "Details" },
    ]);
  });
});

describe("navigation", () => {
  it("offers the mobile device pages", () => {
    wrap(<Sidebar open onClose={() => {}} />);
    expect(screen.getByRole("link", { name: /^devices/i })).toHaveAttribute("href", "/mobile/devices");
    expect(screen.getByRole("link", { name: /approvals/i })).toHaveAttribute("href", "/mobile/approvals");
    expect(screen.getByRole("link", { name: /policies/i })).toHaveAttribute("href", "/mobile/policies");
    expect(screen.getByRole("link", { name: /enrollment/i })).toHaveAttribute("href", "/mobile/enrollments");
    expect(screen.getByRole("link", { name: /^apps/i })).toHaveAttribute("href", "/mobile/apps");
  });
});

describe("devices list", () => {
  it("shows fleet summary, device rows with ownership and compliance, and links to details", () => {
    wrap(
      <DevicesView
        initial={{ items: [device(), device({ id: "d2", displayName: "Work iPhone", platform: "Ios", mode: "UserEnrollment", ownership: "PersonallyOwned", compliance: "NonCompliant", serialNumber: null })], page: 1, pageSize: 25, totalCount: 2, totalPages: 1 }}
        summary={summary}
      />,
    );
    expect(screen.getByText("Managed devices")).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "2" })).toHaveAttribute("href", "/mobile/approvals");
    expect(screen.getByRole("link", { name: "Pixel 8" })).toHaveAttribute("href", "/mobile/devices/d1");
    expect(screen.getByText("Personal (BYOD)", { selector: "span" })).toBeInTheDocument();
    expect(screen.getByText("Non-compliant", { selector: "span" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /sync now/i })).toBeInTheDocument();
  });

  it("hides the sync action from members and filters through the API", async () => {
    const user = userEvent.setup();
    const fetchMock = mockFetch((url) => (url.includes("compliance/summary") ? summary : { items: [], page: 1, pageSize: 25, totalCount: 0, totalPages: 0 }));
    wrap(<DevicesView initial={{ items: [device()], page: 1, pageSize: 25, totalCount: 1, totalPages: 1 }} summary={summary} />, "Member");
    expect(screen.queryByRole("button", { name: /sync now/i })).not.toBeInTheDocument();
    await user.selectOptions(screen.getByLabelText("Compliance"), "NonCompliant");
    await waitFor(() => expect(fetchMock).toHaveBeenCalledWith(expect.stringContaining("mobile-devices?compliance=NonCompliant"), expect.anything()));
    expect(await screen.findByText(/no devices match/i)).toBeInTheDocument();
  });
});

describe("command dialog", () => {
  it("only offers commands the device supports and sends non-destructive ones without a reason", async () => {
    const user = userEvent.setup();
    const onDone = vi.fn(async () => {});
    const fetchMock = mockFetch(() => ({ id: "c1", state: "Sent" }));
    wrap(<CommandDialog device={device()} capabilities={caps({ commands: ["Lock", "Reboot"] })} onClose={() => {}} onDone={onDone} />);
    const options = screen.getAllByRole("option").map((o) => o.textContent);
    expect(options).toEqual(["Lock", "Restart"]);
    expect(screen.queryByLabelText(/reason/i)).not.toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: "Send" }));
    await waitFor(() => expect(onDone).toHaveBeenCalled());
    const [, init] = fetchMock.mock.calls[0];
    expect(JSON.parse(String(init?.body))).toMatchObject({ type: "Lock", reason: null });
  });

  it("requires a reason and explains the approval step for destructive commands", async () => {
    const user = userEvent.setup();
    const fetchMock = mockFetch(() => ({ id: "c1", state: "PendingApproval" }));
    wrap(<CommandDialog device={device()} capabilities={caps()} onClose={() => {}} onDone={async () => {}} />);
    await user.selectOptions(screen.getByLabelText("Command"), "Wipe");
    expect(screen.getByText(/erases the whole device/i)).toBeInTheDocument();
    expect(screen.getByText(/until a different administrator approves/i)).toBeInTheDocument();
    expect(screen.getByLabelText(/reason/i)).toBeRequired();
    expect(screen.getByRole("button", { name: "Request approval" })).toBeInTheDocument();
    await user.type(screen.getByLabelText(/reason/i), "Lost on a train");
    await user.click(screen.getByRole("button", { name: "Request approval" }));
    await waitFor(() => expect(fetchMock).toHaveBeenCalled());
    expect(JSON.parse(String(fetchMock.mock.calls[0][1]?.body))).toMatchObject({ type: "Wipe", reason: "Lost on a train" });
  });

  it("states that retiring a personal device leaves personal data alone", async () => {
    const user = userEvent.setup();
    wrap(
      <CommandDialog
        device={device({ ownership: "PersonallyOwned", mode: "WorkProfile" })}
        capabilities={caps({ commands: ["Lock", "Retire"], canFactoryReset: false, canRemoveCorporateDataOnly: true })}
        onClose={() => {}}
        onDone={async () => {}}
      />,
    );
    await user.selectOptions(screen.getByLabelText("Command"), "Retire");
    expect(screen.getByText(/personal data is not touched/i)).toBeInTheDocument();
    expect(screen.queryByRole("option", { name: /wipe/i })).not.toBeInTheDocument();
  });
});

describe("approvals", () => {
  const pending = (over: Partial<CommandDto> = {}): CommandDto => ({
    id: "c1", deviceId: "d1", type: "Wipe", state: "PendingApproval", requiresApproval: true, reason: "Stolen", requestedByUserId: "someone-else",
    decidedByUserId: null, decidedAtUtc: null, decisionNote: null, dispatchedAtUtc: null, completedAtUtc: null, expiresAtUtc: "2026-11-14T00:00:00Z", error: null,
    createdAtUtc: "2026-11-13T00:00:00Z", ...over,
  });

  it("lists pending commands with the device and reason, and posts the decision", async () => {
    const user = userEvent.setup();
    const fetchMock = mockFetch((url) => (url.includes("/approve") ? {} : { items: [], page: 1, pageSize: 100, totalCount: 0, totalPages: 0 }));
    wrap(<ApprovalsView initial={{ items: [pending()], page: 1, pageSize: 100, totalCount: 1, totalPages: 1 }} devices={[device()]} />);
    expect(screen.getByRole("link", { name: "Pixel 8" })).toHaveAttribute("href", "/mobile/devices/d1");
    expect(screen.getByText("Stolen")).toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: "Approve" }));
    await user.type(screen.getByLabelText(/note/i), "verified with owner");
    await user.click(screen.getAllByRole("button", { name: "Approve" }).at(-1)!);
    await waitFor(() => expect(fetchMock.mock.calls.some(([u]) => String(u).endsWith("mobile-commands/c1/approve"))).toBe(true));
    const call = fetchMock.mock.calls.find(([u]) => String(u).endsWith("/approve"))!;
    expect(JSON.parse(String(call[1]?.body))).toEqual({ note: "verified with owner" });
    expect(await screen.findByText(/nothing is waiting/i)).toBeInTheDocument();
  });

  it("warns when approving your own request and hides decisions from members", async () => {
    const user = userEvent.setup();
    const { unmount } = wrap(<ApprovalsView initial={{ items: [pending({ requestedByUserId: "me" })], page: 1, pageSize: 100, totalCount: 1, totalPages: 1 }} devices={[]} />);
    expect(screen.getByText("Requested by you")).toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: "Approve" }));
    expect(screen.getByText(/you requested this yourself/i)).toBeInTheDocument();
    unmount();
    wrap(<ApprovalsView initial={{ items: [pending()], page: 1, pageSize: 100, totalCount: 1, totalPages: 1 }} devices={[]} />, "Member");
    expect(screen.queryByRole("button", { name: "Approve" })).not.toBeInTheDocument();
  });
});

describe("policy form", () => {
  function Harness({ scenario }: { scenario: (typeof scenarios)[number] }) {
    const [value, setValue] = React.useState(emptySettings());
    return <PolicySettingsForm scenario={scenario} value={value} onChange={setValue} errors={{}} />;
  }

  it("always shows the work-to-personal boundary as enforced for personal devices", () => {
    render(<Harness scenario={scenarios[3]} />);
    const box = screen.getByLabelText(/block sharing from work to personal apps/i);
    expect(box).toBeChecked();
    expect(screen.getByText(/always on for personal devices/i)).toBeInTheDocument();
    expect(screen.getByText(/only work data is managed/i)).toBeInTheDocument();
  });

  it("only offers kiosk settings for dedicated devices and platform-appropriate options", () => {
    const { unmount } = render(<Harness scenario={scenarios[1]} />);
    expect(screen.getByLabelText(/allowed packages/i)).toBeInTheDocument();
    expect(screen.getByLabelText(/disable usb file transfer/i)).toBeInTheDocument();
    unmount();
    render(<Harness scenario={scenarios[5]} />);
    expect(screen.queryByLabelText(/allowed packages/i)).not.toBeInTheDocument();
    expect(screen.queryByLabelText(/disable usb file transfer/i)).not.toBeInTheDocument();
  });

  it("adds a Wi-Fi network whose secret is a write-only password field", async () => {
    const user = userEvent.setup();
    render(<Harness scenario={scenarios[0]} />);
    await user.click(screen.getByRole("button", { name: /add network/i }));
    expect(screen.getByLabelText("Password")).toHaveAttribute("type", "password");
    await user.type(screen.getByLabelText(/network name/i), "Corp");
    expect(screen.getByLabelText(/network name/i)).toHaveValue("Corp");
  });
});

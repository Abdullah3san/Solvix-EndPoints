import { beforeEach, describe, expect, it, vi } from "vitest";
import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import * as React from "react";
import { alertHref } from "@/lib/agent-alerts";
import { emptyDeviceFilters, filtersFromSearchParams, toApiQuery } from "@/lib/device-query";
import { parseHostnames } from "@/lib/hostnames";
import { AlertList } from "@/components/features/agent/agent-overview-view";
import { DownloadAgentButton } from "@/components/features/agent/download-agent";
import { StagedComputersView } from "@/components/features/agent/staged-computers-view";
import { NotificationsProvider } from "@/components/providers/notifications-provider";
import { notificationsStore } from "@/components/providers/notifications-store";
import { SessionProvider } from "@/components/providers/session-provider";
import type { BranchDto, DepartmentDto, DeviceAlertDto, PagedResult, StagedComputerDto, StagedComputerSummaryDto } from "@/lib/api/types";

const get = vi.fn();
const post = vi.fn();
const downloadFile = vi.fn();
vi.mock("@/lib/api/client", () => ({
  api: { get: (...a: unknown[]) => get(...a), post: (...a: unknown[]) => post(...a) },
  downloadFile: (...a: unknown[]) => downloadFile(...a),
}));
vi.mock("next/link", () => ({ default: ({ href, children, ...props }: { href: string; children: React.ReactNode }) => <a href={href} {...props}>{children}</a> }));

const session = (role: "Owner" | "Member") => ({
  user: { id: "u1", email: "a@b.test", displayName: "Admin" },
  tenant: { id: "t1", slug: "acme", name: "Acme", role },
});

const branch = (over: Partial<BranchDto> = {}): BranchDto =>
  ({ id: "b1", name: "Berlin", code: "BER", isHeadquarters: false, phone: null, email: null, timeZone: null, isActive: true, address: {} as BranchDto["address"], createdAtUtc: "", updatedAtUtc: "", ...over });
const department = (): DepartmentDto => ({ id: "d1", branchId: "b1", branchName: "Berlin", parentDepartmentId: null, name: "Tills", code: "TILL", description: null, isActive: true, createdAtUtc: "", updatedAtUtc: "" });

function staged(over: Partial<StagedComputerDto>): StagedComputerDto {
  return { id: "s1", hostname: "FIN-LT-0001", branchId: null, branchName: null, departmentId: null, departmentName: null, source: "Manual", status: "Pending", deviceId: null, enrolledAtUtc: null, createdAtUtc: "2026-11-18T10:00:00Z", ...over };
}
const page = (items: StagedComputerDto[]): PagedResult<StagedComputerDto> => ({ items, page: 1, pageSize: 25, totalCount: items.length, totalPages: 1 });
const summary: StagedComputerSummaryDto = { total: 2, pending: 1, enrolled: 1, retired: 0 };

beforeEach(() => {
  get.mockReset();
  post.mockReset();
  downloadFile.mockReset();
  notificationsStore.reset();
});

describe("hostname parsing", () => {
  it("accepts one per line, CSV rows and mixed separators, dropping blanks and a header", () => {
    expect(parseHostnames("FIN-LT-0001\r\nFIN-LT-0002\n\n  HR-PC-3  \n")).toEqual(["FIN-LT-0001", "FIN-LT-0002", "HR-PC-3"]);
    expect(parseHostnames('Computer Name,Domain\n"PC-1",corp.test\nPC-2;corp.test\nPC-3\tcorp.test')).toEqual(["PC-1", "PC-2", "PC-3"]);
    expect(parseHostnames("hostname\nPC-1")).toEqual(["PC-1"]);
    expect(parseHostnames("   \n,\n")).toEqual([]);
    // A host that merely looks like a header word later in the list is kept.
    expect(parseHostnames("PC-1\nname")).toEqual(["PC-1", "name"]);
  });
});

describe("alert links and device filters", () => {
  it("links each alert to the matching list", () => {
    expect(alertHref({ code: "agent_unhealthy" })).toBe("/endpoints/devices?health=Unhealthy");
    expect(alertHref({ code: "not_reporting" })).toBe("/endpoints/devices?notSeenHours=24");
    expect(alertHref({ code: "agent_outdated" })).toBe("/endpoints/devices?updateAvailable=true");
    expect(alertHref({ code: "staged_pending" })).toBe("/endpoints/staged?status=Pending");
    expect(alertHref({ code: "certificate_expiring" })).toBeNull();
  });

  it("round-trips the alert filters through the page URL and the API query, rejecting junk", () => {
    const filters = filtersFromSearchParams({ health: "Unhealthy", notSeenHours: "24", updateAvailable: "true" });
    expect(filters).toMatchObject({ health: "Unhealthy", notSeenHours: 24, updateAvailable: true });
    expect(toApiQuery({ filters, sortBy: "Name", sortDirection: "Asc", page: 1, pageSize: 25 })).toMatchObject({ health: "Unhealthy", notSeenHours: 24, updateAvailable: true });
    expect(filtersFromSearchParams({ health: "Sideways", notSeenHours: "-5", updateAvailable: "yes" })).toEqual(emptyDeviceFilters);
    expect(filtersFromSearchParams({ notSeenHours: "99999999" }).notSeenHours).toBe(0);
  });
});

describe("alert list", () => {
  const alerts: DeviceAlertDto[] = [
    { code: "agent_unhealthy", severity: "Critical", title: "Agents reporting unhealthy", message: "3 agents report an unhealthy state.", count: 3, devices: [{ id: "d1", name: "FIN-LT-0001" }, { id: "d2", name: "HR-PC-2" }] },
    { code: "certificate_expiring", severity: "Warning", title: "Device certificate expiring", message: "1 device's certificate expires within 7 days.", count: 1, devices: [{ id: "d3", name: "OPS-WS-9" }] },
  ];

  it("shows severity, affected devices and a View all link only where a list filter exists", () => {
    render(<AlertList alerts={alerts} />);
    expect(screen.getByText("Critical")).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "FIN-LT-0001" })).toHaveAttribute("href", "/endpoints/devices/d1");
    expect(screen.getByText("and 1 more")).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "View all 3" })).toHaveAttribute("href", "/endpoints/devices?health=Unhealthy");
    expect(screen.getAllByRole("link", { name: /view all/i })).toHaveLength(1);
  });

  it("says so when there is nothing to report", () => {
    render(<AlertList alerts={[]} />);
    expect(screen.getByText("No alerts")).toBeInTheDocument();
  });
});

describe("download agent", () => {
  function renderButton(role: "Owner" | "Member") {
    return render(
      <SessionProvider session={session(role)}>
        <DownloadAgentButton />
      </SessionProvider>,
    );
  }

  it("is offered to administrators only", () => {
    const { unmount } = renderButton("Member");
    expect(screen.queryByRole("button", { name: /download agent/i })).not.toBeInTheDocument();
    unmount();
    renderButton("Owner");
    expect(screen.getByRole("button", { name: /download agent/i })).toBeInTheDocument();
  });

  it("creates a token for the chosen office and downloads the Windows package; other platforms are not available", async () => {
    const user = userEvent.setup();
    get.mockImplementation(async (path: string) => {
      const items = path === "branches" ? [branch()] : path === "departments" ? [department()] : [];
      return { items, page: 1, pageSize: 200, totalCount: items.length, totalPages: 1 };
    });
    post.mockResolvedValue({
      token: "secret-token",
      enrollmentToken: { id: "tok", name: "x", state: "Active", expiresAtUtc: "2026-11-20T10:00:00Z", maxUses: 5, useCount: 0, branchName: "Berlin", departmentName: "Tills", deviceGroupName: null },
    });
    downloadFile.mockResolvedValue("SolvixAgent-acme.zip");
    renderButton("Owner");

    await user.click(screen.getByRole("button", { name: /download agent/i }));
    const dialog = await screen.findByRole("dialog", { name: "Download agent" });
    await within(dialog).findByLabelText("Branch");

    const radios = within(dialog).getAllByRole("radio");
    expect(radios.map((r) => [r.textContent?.includes("Windows"), (r as HTMLButtonElement).disabled])).toEqual([[true, false], [false, true], [false, true]]);
    expect(within(dialog).getByRole("radio", { name: /windows/i })).toHaveAttribute("aria-checked", "true");

    await user.selectOptions(within(dialog).getByLabelText("Branch"), "b1");
    await user.selectOptions(within(dialog).getByLabelText("Department"), "d1");
    const count = within(dialog).getByLabelText("Number of computers");
    await user.clear(count);
    await user.type(count, "5");
    await user.click(within(dialog).getByRole("button", { name: /download agent/i }));

    await waitFor(() => expect(downloadFile).toHaveBeenCalledWith("enrollment-tokens/package", { token: "secret-token" }, "SolvixAgent.zip"));
    expect(post).toHaveBeenCalledWith("enrollment-tokens", expect.objectContaining({ lifetimeHours: 24, maxUses: 5, branchId: "b1", departmentId: "d1", deviceGroupId: null }));
    expect(await within(dialog).findByText("Downloaded SolvixAgent-acme.zip")).toBeInTheDocument();
  });

  it("revokes the token it created when the package cannot be built", async () => {
    const user = userEvent.setup();
    get.mockImplementation(async () => ({ items: [], page: 1, pageSize: 200, totalCount: 0, totalPages: 1 }));
    post.mockImplementation(async (path: string) =>
      path === "enrollment-tokens" ? { token: "secret", enrollmentToken: { id: "tok-9", expiresAtUtc: "2026-11-20T10:00:00Z", maxUses: 10 } } : undefined,
    );
    downloadFile.mockRejectedValue(new Error("The agent package is not configured on this server"));
    renderButton("Owner");

    await user.click(screen.getByRole("button", { name: /download agent/i }));
    const dialog = await screen.findByRole("dialog", { name: "Download agent" });
    await within(dialog).findByLabelText("Branch");
    await user.click(within(dialog).getByRole("button", { name: /download agent/i }));

    expect(await within(dialog).findByText(/not configured on this server/i)).toBeInTheDocument();
    expect(post).toHaveBeenCalledWith("enrollment-tokens/tok-9/revoke");
  });

  it("offers a retry when the branches cannot be loaded", async () => {
    const user = userEvent.setup();
    get.mockRejectedValue(new Error("offline"));
    renderButton("Owner");
    await user.click(screen.getByRole("button", { name: /download agent/i }));
    expect(await screen.findByRole("button", { name: /try again/i })).toBeInTheDocument();
    expect(screen.queryByLabelText("Branch")).not.toBeInTheDocument();
  });
});

describe("staged computers", () => {
  function renderView(items: StagedComputerDto[], role: "Owner" | "Member" = "Owner") {
    return render(
      <SessionProvider session={session(role)}>
        <NotificationsProvider>
          <StagedComputersView initial={page(items)} summary={summary} branches={[branch()]} departments={[department()]} />
        </NotificationsProvider>
      </SessionProvider>,
    );
  }

  it("flags computers without the agent and links enrolled ones to their device", () => {
    renderView([staged({}), staged({ id: "s2", hostname: "HR-PC-0002", status: "Enrolled", deviceId: "dev-2", enrolledAtUtc: "2026-11-18T12:00:00Z" })]);
    const table = screen.getByRole("table", { name: "Staged computers" });
    expect(within(table).getByText("Agent not installed")).toBeInTheDocument();
    expect(within(table).getByText("Enrolled")).toBeInTheDocument();
    expect(within(table).getByRole("link", { name: "HR-PC-0002" })).toHaveAttribute("href", "/endpoints/devices/dev-2");
    expect(within(table).queryByRole("link", { name: "FIN-LT-0001" })).not.toBeInTheDocument();
  });

  it("is read-only for members", () => {
    renderView([staged({})], "Member");
    expect(screen.queryByRole("button", { name: /import/i })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: /add computer/i })).not.toBeInTheDocument();
    expect(screen.queryByRole("checkbox", { name: /select row/i })).not.toBeInTheDocument();
  });

  it("imports a pasted list with a placement and reports what was skipped", async () => {
    const user = userEvent.setup();
    post.mockResolvedValue({ created: 2, alreadyStaged: 1, rejected: [{ value: "bad name", reason: "Not a valid hostname" }] });
    get.mockResolvedValue(page([]));
    renderView([]);

    await user.click(screen.getAllByRole("button", { name: /import/i })[0]);
    const dialog = screen.getByRole("dialog", { name: "Import expected computers" });
    await user.click(within(dialog).getByLabelText("Computer names"));
    await user.paste("PC-1\nPC-2\nPC-1x\n");
    expect(within(dialog).getByText("3 found")).toBeInTheDocument();
    await user.selectOptions(within(dialog).getByLabelText("Branch"), "b1");
    await user.click(within(dialog).getByRole("button", { name: "Import 3 computers" }));

    expect(post).toHaveBeenCalledWith("staged-computers/import", { hostnames: ["PC-1", "PC-2", "PC-1x"], branchId: "b1", departmentId: null });
    expect(await within(dialog).findByText("2 added")).toBeInTheDocument();
    expect(within(dialog).getByText("bad name")).toBeInTheDocument();
  });

  it("refuses an import over the limit before calling the API", async () => {
    const user = userEvent.setup();
    renderView([]);
    await user.click(screen.getAllByRole("button", { name: /import/i })[0]);
    const dialog = screen.getByRole("dialog", { name: "Import expected computers" });
    await user.click(within(dialog).getByLabelText("Computer names"));
    await user.paste(Array.from({ length: 1001 }, (_, i) => `PC-${i}`).join("\n"));
    expect(within(dialog).getByText(/at most 1000 computers/i)).toBeInTheDocument();
    expect(within(dialog).getByRole("button", { name: /^import/i })).toBeDisabled();
    expect(post).not.toHaveBeenCalled();
  });

  it("confirms before removing entries and says enrolled computers are not affected", async () => {
    const user = userEvent.setup();
    post.mockResolvedValue(undefined);
    get.mockResolvedValue(page([]));
    renderView([staged({})]);

    await user.click(screen.getByRole("button", { name: "Actions for FIN-LT-0001" }));
    await user.click(screen.getByRole("menuitem", { name: /remove from list/i }));
    const dialog = screen.getByRole("alertdialog", { name: "Remove FIN-LT-0001?" });
    expect(post).not.toHaveBeenCalled();
    expect(within(dialog).getByText(/stay enrolled/i)).toBeInTheDocument();
    await user.click(within(dialog).getByRole("button", { name: "Remove from list" }));
    expect(post).toHaveBeenCalledWith("staged-computers/remove", { ids: ["s1"] });
  });
});

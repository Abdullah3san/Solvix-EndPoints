import { describe, expect, it, vi } from "vitest";
import { render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import * as React from "react";
import { generatePassword, meetsPasswordPolicy } from "@/lib/passwords";
import { CompaniesView } from "@/components/features/platform/companies-view";
import { NotificationsProvider } from "@/components/providers/notifications-provider";
import { notificationsStore } from "@/components/providers/notifications-store";
import type { PagedResult, PlatformSummaryDto, PlatformTenantDto } from "@/lib/api/types";

const post = vi.fn();
const get = vi.fn();
vi.mock("@/lib/api/client", () => ({
  api: { get: (...args: unknown[]) => get(...args), post: (...args: unknown[]) => post(...args) },
}));

function company(overrides: Partial<PlatformTenantDto>): PlatformTenantDto {
  return {
    id: "c1",
    name: "Acme Subscribers",
    slug: "acme",
    status: "Active",
    createdAtUtc: "2026-11-01T00:00:00Z",
    suspendedAtUtc: null,
    memberCount: 3,
    deviceCount: 12,
    ownerEmails: ["it@acme.test"],
    isCurrent: false,
    ...overrides,
  };
}

const page = (items: PlatformTenantDto[]): PagedResult<PlatformTenantDto> => ({ items, page: 1, pageSize: 25, totalCount: items.length, totalPages: 1 });
const summary: PlatformSummaryDto = { total: 2, active: 2, suspended: 0, publicRegistrationOpen: false };

function renderView(items: PlatformTenantDto[], overrides: Partial<PlatformSummaryDto> = {}) {
  return render(
    <NotificationsProvider>
      <CompaniesView initial={page(items)} summary={{ ...summary, ...overrides }} />
    </NotificationsProvider>,
  );
}

describe("generated passwords", () => {
  it("always satisfy the API policy and are not repeated", () => {
    const seen = new Set<string>();
    for (let i = 0; i < 200; i++) {
      const password = generatePassword();
      expect(password).toHaveLength(16);
      expect(meetsPasswordPolicy(password)).toBe(true);
      seen.add(password);
    }
    expect(seen.size).toBe(200);
    expect(generatePassword(4)).toHaveLength(10);
  });

  it("checks the password policy", () => {
    expect(meetsPasswordPolicy("short1")).toBe(false);
    expect(meetsPasswordPolicy("longenoughbutnodigit")).toBe(false);
    expect(meetsPasswordPolicy("1234567890123")).toBe(false);
    expect(meetsPasswordPolicy("Passw0rd!Secure")).toBe(true);
  });
});

describe("provider console", () => {
  it("lists companies with real counts and protects the operator's own workspace", async () => {
    const user = userEvent.setup();
    notificationsStore.reset();
    renderView([company({}), company({ id: "own", name: "Platform", slug: "platform", isCurrent: true, memberCount: 1, deviceCount: 0, ownerEmails: ["operator@platform.test"] })]);

    const table = screen.getByRole("table", { name: "Companies" });
    expect(within(table).getByText("Acme Subscribers")).toBeInTheDocument();
    expect(within(table).getByText("it@acme.test")).toBeInTheDocument();
    expect(within(table).getByText("Your workspace")).toBeInTheDocument();

    await user.click(screen.getByRole("button", { name: "Actions for Platform" }));
    expect(screen.getByRole("menuitem", { name: /cannot suspend your own workspace/i })).toHaveAttribute("aria-disabled", "true");
  });

  it("asks for confirmation before suspending and reports the result", async () => {
    const user = userEvent.setup();
    notificationsStore.reset();
    post.mockReset().mockResolvedValue(company({ status: "Suspended" }));
    get.mockReset().mockResolvedValue(page([company({ status: "Suspended" })]));
    renderView([company({})]);

    await user.click(screen.getByRole("button", { name: "Actions for Acme Subscribers" }));
    await user.click(screen.getByRole("menuitem", { name: /suspend/i }));
    const dialog = screen.getByRole("alertdialog", { name: "Suspend Acme Subscribers?" });
    expect(post).not.toHaveBeenCalled();
    expect(within(dialog).getByText(/12 device/)).toBeInTheDocument();

    await user.click(within(dialog).getByRole("button", { name: "Suspend company" }));
    expect(post).toHaveBeenCalledWith("platform/tenants/c1/suspend");
  });

  it("validates the new company form before calling the API and hands over the credentials once", async () => {
    const user = userEvent.setup();
    notificationsStore.reset();
    post.mockReset().mockResolvedValue(company({ id: "new", name: "Globex", slug: "globex", ownerEmails: ["owner@globex.test"] }));
    get.mockReset().mockResolvedValue(page([]));
    get.mockResolvedValueOnce(page([])).mockResolvedValueOnce(summary);
    renderView([]);

    await user.click(screen.getAllByRole("button", { name: /new company/i })[0]);
    const dialog = screen.getByRole("dialog", { name: "New company" });
    await user.click(within(dialog).getByRole("button", { name: "Create company" }));
    expect(post).not.toHaveBeenCalled();
    expect(within(dialog).getByText("Company name is required")).toBeInTheDocument();
    expect(within(dialog).getByText("Enter a valid e-mail address")).toBeInTheDocument();

    await user.type(within(dialog).getByLabelText(/company name/i), "Globex Corp");
    expect(within(dialog).getByLabelText(/workspace id/i)).toHaveValue("globex-corp");
    await user.type(within(dialog).getByLabelText(/owner's name/i), "Gina Globex");
    await user.type(within(dialog).getByLabelText(/owner's e-mail/i), "owner@globex.test");
    const password = (within(dialog).getByLabelText(/initial password/i) as HTMLInputElement).value;
    expect(meetsPasswordPolicy(password)).toBe(true);
    await user.click(within(dialog).getByRole("button", { name: "Create company" }));

    expect(post).toHaveBeenCalledWith("platform/tenants", {
      companyName: "Globex Corp",
      slug: "globex-corp",
      ownerEmail: "owner@globex.test",
      ownerDisplayName: "Gina Globex",
      ownerPassword: password,
    });
    const handover = await screen.findByRole("dialog", { name: /globex is ready/i });
    expect(within(handover).getByText(password)).toBeInTheDocument();
    expect(within(handover).getByText("owner@globex.test")).toBeInTheDocument();
  });

  it("warns while public registration is open", () => {
    renderView([company({})], { publicRegistrationOpen: true });
    expect(screen.getByText("Public registration is open")).toBeInTheDocument();
  });

  it("shows an empty state that points to creating the first company", () => {
    renderView([]);
    expect(screen.getByText("No companies yet")).toBeInTheDocument();
  });
});

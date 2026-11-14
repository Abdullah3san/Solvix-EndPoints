import { describe, expect, it, vi } from "vitest";
import { render, screen } from "@testing-library/react";
import { Sidebar } from "@/components/shell/sidebar";
import { SessionProvider, type Session } from "@/components/providers/session-provider";
import type { Permission } from "@/lib/api/types";

vi.mock("next/navigation", () => ({ usePathname: () => "/organization/departments" }));

function session(permissions: Permission[], mfaEnrollmentRequired = false): Session {
  return {
    user: { id: "u1", email: "owner@acme.test", displayName: "Owner" },
    tenant: { id: "t1", slug: "acme", name: "Acme Corp" },
    sessionId: "s1",
    roles: ["Custom"],
    permissions,
    mfaEnabled: false,
    mfaVerified: false,
    mfaEnrollmentRequired,
  };
}

function renderSidebar(s: Session) {
  return render(
    <SessionProvider session={s}>
      <Sidebar open onClose={() => {}} />
    </SessionProvider>,
  );
}

describe("sidebar", () => {
  it("shows the tenant and highlights the active navigation item", () => {
    renderSidebar(session(["organization.view", "users.view", "roles.view", "security.view", "audit.view"]));
    expect(screen.getByText("Acme Corp")).toBeInTheDocument();
    expect(screen.getByText("acme")).toBeInTheDocument();
    expect(screen.getByRole("link", { name: /departments/i })).toHaveAttribute("aria-current", "page");
    expect(screen.getByRole("link", { name: /branches/i })).not.toHaveAttribute("aria-current");
    expect(screen.getByRole("link", { name: /dashboard/i })).toHaveAttribute("href", "/dashboard");
    expect(screen.getByRole("link", { name: /audit log/i })).toHaveAttribute("href", "/settings/audit");
  });

  it("only lists what the user's permissions allow", () => {
    renderSidebar(session(["users.view"]));
    expect(screen.getByRole("link", { name: /^users$/i })).toBeInTheDocument();
    expect(screen.queryByRole("link", { name: /branches/i })).toBeNull();
    expect(screen.queryByRole("link", { name: /roles/i })).toBeNull();
    expect(screen.queryByRole("link", { name: /security policy/i })).toBeNull();
    expect(screen.queryByText("Organization")).toBeNull();
    expect(screen.getByRole("link", { name: /my account/i })).toBeInTheDocument();
  });

  it("hides everything but the account pages while MFA enrolment is required", () => {
    renderSidebar(session(["organization.view", "users.view", "roles.view"], true));
    expect(screen.queryByRole("link", { name: /^users$/i })).toBeNull();
    expect(screen.queryByRole("link", { name: /branches/i })).toBeNull();
    expect(screen.getByRole("link", { name: /my account/i })).toBeInTheDocument();
  });

  it("shows endpoint management and hides enrollment without devices.enroll", () => {
    const { unmount } = renderSidebar(session(["devices.view", "devices.enroll"]));
    expect(screen.getByRole("link", { name: /^devices$/i })).toHaveAttribute("href", "/endpoints/devices");
    expect(screen.getByRole("link", { name: /device groups/i })).toHaveAttribute("href", "/endpoints/groups");
    expect(screen.getByRole("link", { name: /enrollment/i })).toHaveAttribute("href", "/endpoints/enrollment");
    unmount();

    renderSidebar(session(["devices.view"]));
    expect(screen.getByRole("link", { name: /^devices$/i })).toBeInTheDocument();
    expect(screen.queryByRole("link", { name: /enrollment/i })).not.toBeInTheDocument();
  });

  it("shows asset pages by permission and 'My assets' to everyone", () => {
    const { unmount } = renderSidebar(session(["assets.view", "reports.view", "assets.edit"]));
    expect(screen.getByRole("link", { name: /asset register/i })).toHaveAttribute("href", "/assets");
    expect(screen.getByRole("link", { name: /endpoint matching/i })).toHaveAttribute("href", "/assets/matching");
    expect(screen.getByRole("link", { name: /^reports$/i })).toHaveAttribute("href", "/assets/reports");
    expect(screen.getByRole("link", { name: /asset management/i })).toHaveAttribute("href", "/settings/assets");
    expect(screen.getByRole("link", { name: /my assets/i })).toHaveAttribute("href", "/my-assets");
    unmount();

    renderSidebar(session([]));
    expect(screen.queryByRole("link", { name: /asset register/i })).not.toBeInTheDocument();
    expect(screen.queryByRole("link", { name: /physical audits/i })).not.toBeInTheDocument();
    expect(screen.queryByRole("link", { name: /asset management/i })).not.toBeInTheDocument();
    expect(screen.getByRole("link", { name: /my assets/i })).toBeInTheDocument();
  });
});

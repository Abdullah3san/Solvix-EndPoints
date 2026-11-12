import { describe, expect, it, vi } from "vitest";
import { render, screen } from "@testing-library/react";
import { Sidebar } from "@/components/shell/sidebar";
import { SessionProvider } from "@/components/providers/session-provider";

vi.mock("next/navigation", () => ({ usePathname: () => "/organization/departments" }));

const session = {
  user: { id: "u1", email: "owner@acme.test", displayName: "Owner" },
  tenant: { id: "t1", slug: "acme", name: "Acme Corp", role: "Owner" as const },
};

describe("sidebar", () => {
  it("shows the tenant and highlights the active navigation item", () => {
    render(
      <SessionProvider session={session}>
        <Sidebar open onClose={() => {}} />
      </SessionProvider>,
    );
    expect(screen.getByText("Acme Corp")).toBeInTheDocument();
    expect(screen.getByText("acme")).toBeInTheDocument();
    expect(screen.getByRole("link", { name: /departments/i })).toHaveAttribute("aria-current", "page");
    expect(screen.getByRole("link", { name: /branches/i })).not.toHaveAttribute("aria-current");
    expect(screen.getByRole("link", { name: /dashboard/i })).toHaveAttribute("href", "/dashboard");
  });

  it("shows endpoint management and hides enrollment from members", () => {
    const { unmount } = render(
      <SessionProvider session={session}>
        <Sidebar open onClose={() => {}} />
      </SessionProvider>,
    );
    expect(screen.getByRole("link", { name: /^devices$/i })).toHaveAttribute("href", "/endpoints/devices");
    expect(screen.getByRole("link", { name: /device groups/i })).toHaveAttribute("href", "/endpoints/groups");
    expect(screen.getByRole("link", { name: /enrollment/i })).toHaveAttribute("href", "/endpoints/enrollment");
    unmount();

    render(
      <SessionProvider session={{ ...session, tenant: { ...session.tenant, role: "Member" as const } }}>
        <Sidebar open onClose={() => {}} />
      </SessionProvider>,
    );
    expect(screen.getByRole("link", { name: /^devices$/i })).toBeInTheDocument();
    expect(screen.queryByRole("link", { name: /enrollment/i })).not.toBeInTheDocument();
  });

  it("shows the asset register to admins and only 'My assets' to members", () => {
    const { unmount } = render(
      <SessionProvider session={session}>
        <Sidebar open onClose={() => {}} />
      </SessionProvider>,
    );
    expect(screen.getByRole("link", { name: /asset register/i })).toHaveAttribute("href", "/assets");
    expect(screen.getByRole("link", { name: /endpoint matching/i })).toHaveAttribute("href", "/assets/matching");
    expect(screen.getByRole("link", { name: /my assets/i })).toHaveAttribute("href", "/my-assets");
    unmount();

    render(
      <SessionProvider session={{ ...session, tenant: { ...session.tenant, role: "Member" as const } }}>
        <Sidebar open onClose={() => {}} />
      </SessionProvider>,
    );
    expect(screen.queryByRole("link", { name: /asset register/i })).not.toBeInTheDocument();
    expect(screen.queryByRole("link", { name: /physical audits/i })).not.toBeInTheDocument();
    expect(screen.getByRole("link", { name: /my assets/i })).toBeInTheDocument();
  });
});

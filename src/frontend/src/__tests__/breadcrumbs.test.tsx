import { describe, expect, it, vi } from "vitest";
import { render, screen } from "@testing-library/react";
import { Breadcrumbs, crumbsFromPathname } from "@/components/shell/breadcrumbs";

vi.mock("next/navigation", () => ({ usePathname: () => "/organization/branches" }));

describe("breadcrumbs", () => {
  it("maps known segments to labels and links all but the last", () => {
    expect(crumbsFromPathname("/organization/departments")).toEqual([
      { label: "Organization", href: "/organization" },
      { label: "Departments" },
    ]);
    expect(crumbsFromPathname("/settings/workspace/some-thing")).toEqual([
      { label: "Settings", href: "/settings" },
      { label: "Workspace", href: "/settings/workspace" },
      { label: "Some Thing" },
    ]);
  });

  it("renders the current page without a link", () => {
    render(<Breadcrumbs />);
    const current = screen.getByText("Branches");
    expect(current).toHaveAttribute("aria-current", "page");
    expect(screen.getByRole("link", { name: "Organization" })).toHaveAttribute("href", "/organization");
    expect(screen.getByRole("link", { name: "Home" })).toHaveAttribute("href", "/dashboard");
  });
});

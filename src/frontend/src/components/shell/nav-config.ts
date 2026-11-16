import type { LucideIcon } from "lucide-react";
import { Boxes, Building2, GitBranch, KeyRound, LayoutDashboard, Monitor, Network, Palette, Settings2 } from "lucide-react";

export interface NavItem {
  label: string;
  href: string;
  icon: LucideIcon;
  /** Restrict visibility to tenant admins/owners. */
  adminOnly?: boolean;
  /** Only shown under `next dev` (internal tooling such as the design system reference). */
  devOnly?: boolean;
  /** Extra words the global search matches on. */
  keywords?: string;
}

export interface NavSection {
  title?: string;
  items: NavItem[];
}

/** Single source of truth for the sidebar, the global search and breadcrumbs. Only real routes belong here. */
export const navigation: NavSection[] = [
  {
    items: [{ label: "Dashboard", href: "/dashboard", icon: LayoutDashboard, keywords: "overview home" }],
  },
  {
    title: "Endpoints",
    items: [
      { label: "Devices", href: "/endpoints/devices", icon: Monitor, keywords: "computers inventory fleet" },
      { label: "Device groups", href: "/endpoints/groups", icon: Boxes },
      { label: "Enrollment", href: "/endpoints/enrollment", icon: KeyRound, adminOnly: true, keywords: "token agent install" },
    ],
  },
  {
    title: "Organization",
    items: [
      { label: "Company profile", href: "/organization/company", icon: Building2 },
      { label: "Branches", href: "/organization/branches", icon: GitBranch, keywords: "sites locations" },
      { label: "Departments", href: "/organization/departments", icon: Network },
    ],
  },
  {
    title: "Settings",
    items: [
      { label: "Workspace", href: "/settings/workspace", icon: Settings2, keywords: "members roles" },
      { label: "Design system", href: "/design-system", icon: Palette, devOnly: true, keywords: "components tokens" },
    ],
  },
];

export function visibleNavigation(options: { isAdmin: boolean; isDevelopment: boolean }): NavSection[] {
  return navigation
    .map((section) => ({
      ...section,
      items: section.items.filter((item) => (!item.adminOnly || options.isAdmin) && (!item.devOnly || options.isDevelopment)),
    }))
    .filter((section) => section.items.length > 0);
}

/** Human-readable labels for breadcrumb segments that are not navigation items. */
export const segmentLabels: Record<string, string> = {
  dashboard: "Dashboard",
  organization: "Organization",
  company: "Company profile",
  branches: "Branches",
  departments: "Departments",
  settings: "Settings",
  workspace: "Workspace",
  endpoints: "Endpoints",
  devices: "Devices",
  groups: "Device groups",
  enrollment: "Enrollment",
  "design-system": "Design system",
};

export function isActivePath(pathname: string, href: string): boolean {
  return pathname === href || pathname.startsWith(`${href}/`);
}

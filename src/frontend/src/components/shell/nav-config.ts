import type { LucideIcon } from "lucide-react";
import { Activity, Boxes, Building2, ClipboardList, Landmark, GitBranch, KeyRound, LayoutDashboard, Monitor, Network, Palette, Settings2 } from "lucide-react";

export interface NavItem {
  label: string;
  href: string;
  icon: LucideIcon;
  /** Restrict visibility to tenant admins/owners. */
  adminOnly?: boolean;
  /** Only shown to the provider's platform operator. */
  platformOnly?: boolean;
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
      { label: "Agent overview", href: "/endpoints/agents", icon: Activity, keywords: "agents health versions platform alerts installation status" },
      { label: "Devices", href: "/endpoints/devices", icon: Monitor, keywords: "computers inventory fleet" },
      { label: "Staged computers", href: "/endpoints/staged", icon: ClipboardList, keywords: "expected yet to install import pending agent not installed" },
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
    title: "Platform",
    items: [{ label: "Companies", href: "/platform/companies", icon: Landmark, platformOnly: true, keywords: "customers tenants subscribers workspaces" }],
  },
  {
    title: "Settings",
    items: [
      { label: "Workspace", href: "/settings/workspace", icon: Settings2, keywords: "members roles" },
      { label: "Design system", href: "/design-system", icon: Palette, devOnly: true, keywords: "components tokens" },
    ],
  },
];

export function visibleNavigation(options: { isAdmin: boolean; isDevelopment: boolean; isPlatformAdmin?: boolean }): NavSection[] {
  return navigation
    .map((section) => ({
      ...section,
      items: section.items.filter(
        (item) => (!item.adminOnly || options.isAdmin) && (!item.devOnly || options.isDevelopment) && (!item.platformOnly || options.isPlatformAdmin === true),
      ),
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
  agents: "Agent overview",
  staged: "Staged computers",
  groups: "Device groups",
  enrollment: "Enrollment",
  platform: "Platform",
  companies: "Companies",
  "design-system": "Design system",
};

export function isActivePath(pathname: string, href: string): boolean {
  return pathname === href || pathname.startsWith(`${href}/`);
}

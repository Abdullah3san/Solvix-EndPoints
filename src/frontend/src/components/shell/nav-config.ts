import type { LucideIcon } from "lucide-react";
import { Boxes, Building2, GitBranch, KeyRound, LayoutDashboard, Monitor, Network, Settings2 } from "lucide-react";

export interface NavItem {
  label: string;
  href: string;
  icon: LucideIcon;
  /** Restrict visibility to tenant admins/owners. */
  adminOnly?: boolean;
}

export interface NavSection {
  title?: string;
  items: NavItem[];
}

export const navigation: NavSection[] = [
  {
    items: [{ label: "Dashboard", href: "/dashboard", icon: LayoutDashboard }],
  },
  {
    title: "Organization",
    items: [
      { label: "Company profile", href: "/organization/company", icon: Building2 },
      { label: "Branches", href: "/organization/branches", icon: GitBranch },
      { label: "Departments", href: "/organization/departments", icon: Network },
    ],
  },
  {
    title: "Endpoints",
    items: [
      { label: "Devices", href: "/endpoints/devices", icon: Monitor },
      { label: "Device groups", href: "/endpoints/groups", icon: Boxes },
      { label: "Enrollment", href: "/endpoints/enrollment", icon: KeyRound, adminOnly: true },
    ],
  },
  {
    title: "Settings",
    items: [{ label: "Workspace", href: "/settings/workspace", icon: Settings2 }],
  },
];

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
};

export function isActivePath(pathname: string, href: string): boolean {
  return pathname === href || pathname.startsWith(`${href}/`);
}

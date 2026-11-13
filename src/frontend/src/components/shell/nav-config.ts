import type { LucideIcon } from "lucide-react";
import { AppWindow, Building2, GitBranch, LayoutDashboard, Network, QrCode, Settings2, ShieldCheck, SlidersHorizontal, Smartphone } from "lucide-react";

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
    title: "Mobile devices",
    items: [
      { label: "Devices", href: "/mobile/devices", icon: Smartphone },
      { label: "Approvals", href: "/mobile/approvals", icon: ShieldCheck },
      { label: "Policies", href: "/mobile/policies", icon: SlidersHorizontal },
      { label: "Apps", href: "/mobile/apps", icon: AppWindow },
      { label: "Enrollment", href: "/mobile/enrollments", icon: QrCode },
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
  mobile: "Mobile devices",
  devices: "Devices",
  approvals: "Approvals",
  policies: "Policies",
  apps: "Apps",
  enrollments: "Enrollment",
  settings: "Settings",
  workspace: "Workspace",
};

export function isActivePath(pathname: string, href: string): boolean {
  return pathname === href || pathname.startsWith(`${href}/`);
}

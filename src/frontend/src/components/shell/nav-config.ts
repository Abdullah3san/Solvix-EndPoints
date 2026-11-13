import type { LucideIcon } from "lucide-react";
import {
  Building2,
  CalendarClock,
  GitBranch,
  LayoutDashboard,
  Laptop,
  Network,
  Rocket,
  ScrollText,
  Settings2,
  ShieldCheck,
  SlidersHorizontal,
} from "lucide-react";

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
      { label: "Devices", href: "/endpoints/devices", icon: Laptop },
      { label: "Patches", href: "/endpoints/patches", icon: ShieldCheck },
      { label: "Deployments", href: "/endpoints/deployments", icon: Rocket },
      { label: "Maintenance windows", href: "/endpoints/maintenance-windows", icon: CalendarClock },
      { label: "Configuration", href: "/endpoints/configuration", icon: SlidersHorizontal },
    ],
  },
  {
    title: "Settings",
    items: [
      { label: "Workspace", href: "/settings/workspace", icon: Settings2 },
      { label: "Audit log", href: "/settings/audit", icon: ScrollText, adminOnly: true },
    ],
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
  patches: "Patches",
  deployments: "Deployments",
  "maintenance-windows": "Maintenance windows",
  configuration: "Configuration",
  audit: "Audit log",
};

export function isActivePath(pathname: string, href: string): boolean {
  return pathname === href || pathname.startsWith(`${href}/`);
}

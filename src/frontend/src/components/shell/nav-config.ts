import type { LucideIcon } from "lucide-react";
import {
  Building2,
  GitBranch,
  KeyRound,
  LayoutDashboard,
  Network,
  ScrollText,
  Settings2,
  ShieldCheck,
  UserRoundCog,
  Users,
} from "lucide-react";
import type { Permission } from "@/lib/api/types";

export interface NavItem {
  label: string;
  href: string;
  icon: LucideIcon;
  /** Shown only to users holding this permission. The API enforces it regardless. */
  permission?: Permission;
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
      { label: "Company profile", href: "/organization/company", icon: Building2, permission: "organization.view" },
      { label: "Branches", href: "/organization/branches", icon: GitBranch, permission: "organization.view" },
      { label: "Departments", href: "/organization/departments", icon: Network, permission: "organization.view" },
    ],
  },
  {
    title: "Access",
    items: [
      { label: "Users", href: "/settings/users", icon: Users, permission: "users.view" },
      { label: "Roles", href: "/settings/roles", icon: UserRoundCog, permission: "roles.view" },
      { label: "Security policy", href: "/settings/security", icon: ShieldCheck, permission: "security.view" },
      { label: "Audit log", href: "/settings/audit", icon: ScrollText, permission: "audit.view" },
    ],
  },
  {
    title: "Settings",
    items: [
      { label: "Workspace", href: "/settings/workspace", icon: Settings2 },
      { label: "My account", href: "/account/security", icon: KeyRound },
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
  users: "Users",
  roles: "Roles",
  security: "Security",
  audit: "Audit log",
  account: "My account",
};

export function isActivePath(pathname: string, href: string): boolean {
  return pathname === href || pathname.startsWith(`${href}/`);
}

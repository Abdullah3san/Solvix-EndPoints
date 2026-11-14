import type { LucideIcon } from "lucide-react";
import {
  Activity,
  BarChart3,
  Boxes,
  Building2,
  ClipboardCheck,
  GitBranch,
  GitMerge,
  KeyRound,
  LayoutDashboard,
  Monitor,
  Network,
  Package,
  PackageCheck,
  PackageOpen,
  Rocket,
  ScrollText,
  Settings2,
  ShieldBan,
  ShieldCheck,
  SlidersHorizontal,
  Store,
  UserRoundCog,
  Users,
  Wrench,
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
    title: "Endpoints",
    items: [
      { label: "Monitoring", href: "/endpoints/monitoring", icon: Activity, permission: "devices.view" },
      { label: "Devices", href: "/endpoints/devices", icon: Monitor, permission: "devices.view" },
      { label: "Device groups", href: "/endpoints/groups", icon: Boxes, permission: "devices.view" },
      { label: "Enrollment", href: "/endpoints/enrollment", icon: KeyRound, permission: "devices.enroll" },
    ],
  },
  {
    title: "Software",
    items: [
      { label: "Deployments", href: "/software/deployments", icon: Rocket, permission: "software.view" },
      { label: "Packages", href: "/software/packages", icon: PackageOpen, permission: "software.view" },
      { label: "Inventory", href: "/software/inventory", icon: ClipboardCheck, permission: "software.view" },
      { label: "Policies", href: "/software/policies", icon: ShieldBan, permission: "software.view" },
    ],
  },
  {
    title: "Assets",
    items: [
      { label: "Asset register", href: "/assets", icon: Package, permission: "assets.view" },
      { label: "Endpoint matching", href: "/assets/matching", icon: GitMerge, permission: "assets.view" },
      { label: "Physical audits", href: "/assets/audits", icon: ClipboardCheck, permission: "assets.view" },
      { label: "Maintenance", href: "/assets/maintenance", icon: Wrench, permission: "assets.view" },
      { label: "Vendors", href: "/assets/vendors", icon: Store, permission: "assets.view" },
      { label: "Reports", href: "/assets/reports", icon: BarChart3, permission: "reports.view" },
      { label: "My assets", href: "/my-assets", icon: PackageCheck },
    ],
  },
  {
    title: "Settings",
    items: [
      { label: "Workspace", href: "/settings/workspace", icon: Settings2 },
      { label: "Asset management", href: "/settings/assets", icon: SlidersHorizontal, permission: "assets.edit" },
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
  endpoints: "Endpoints",
  devices: "Devices",
  groups: "Device groups",
  enrollment: "Enrollment",
  monitoring: "Monitoring",
  availability: "Availability",
  users: "Users",
  roles: "Roles",
  security: "Security",
  audit: "Audit log",
  account: "My account",
  software: "Software",
  packages: "Packages",
  deployments: "Deployments",
  inventory: "Inventory",
  policies: "Policies",
  new: "New",
  assets: "Assets",
  matching: "Endpoint matching",
  audits: "Physical audits",
  maintenance: "Maintenance",
  vendors: "Vendors",
  reports: "Reports",
  labels: "Labels",
  "my-assets": "My assets",
};

export function isActivePath(pathname: string, href: string): boolean {
  return pathname === href || pathname.startsWith(`${href}/`);
}

/**
 * The navigation href that best matches the path: the longest matching one,
 * so /assets/matching highlights "Endpoint matching" and not also "Asset register".
 */
export function activeHref(pathname: string, sections: NavSection[] = navigation): string | null {
  let best: string | null = null;
  for (const item of sections.flatMap((s) => s.items)) {
    if (isActivePath(pathname, item.href) && (best === null || item.href.length > best.length)) best = item.href;
  }
  return best;
}

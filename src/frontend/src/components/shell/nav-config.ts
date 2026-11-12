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
  Settings2,
  SlidersHorizontal,
  Store,
  Wrench,
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
      { label: "Monitoring", href: "/endpoints/monitoring", icon: Activity },
      { label: "Devices", href: "/endpoints/devices", icon: Monitor },
      { label: "Device groups", href: "/endpoints/groups", icon: Boxes },
      { label: "Enrollment", href: "/endpoints/enrollment", icon: KeyRound, adminOnly: true },
    ],
  },
  {
    title: "Assets",
    items: [
      { label: "Asset register", href: "/assets", icon: Package, adminOnly: true },
      { label: "Endpoint matching", href: "/assets/matching", icon: GitMerge, adminOnly: true },
      { label: "Physical audits", href: "/assets/audits", icon: ClipboardCheck, adminOnly: true },
      { label: "Maintenance", href: "/assets/maintenance", icon: Wrench, adminOnly: true },
      { label: "Vendors", href: "/assets/vendors", icon: Store, adminOnly: true },
      { label: "Reports", href: "/assets/reports", icon: BarChart3, adminOnly: true },
      { label: "My assets", href: "/my-assets", icon: PackageCheck },
    ],
  },
  {
    title: "Settings",
    items: [
      { label: "Workspace", href: "/settings/workspace", icon: Settings2 },
      { label: "Asset management", href: "/settings/assets", icon: SlidersHorizontal, adminOnly: true },
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
  assets: "Assets",
  matching: "Endpoint matching",
  audits: "Physical audits",
  maintenance: "Maintenance",
  vendors: "Vendors",
  reports: "Reports",
  labels: "Labels",
  new: "Register asset",
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

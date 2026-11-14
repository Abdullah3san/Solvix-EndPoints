import type { LucideIcon } from "lucide-react";
import { AlertTriangle, BookOpen, Building2, Bug, ClipboardCheck, GitBranch, LayoutDashboard, Laptop, Network, ScrollText, Settings2, ShieldCheck, FileCheck } from "lucide-react";

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
    title: "Endpoint security",
    items: [
      { label: "Overview", href: "/security", icon: ShieldCheck, adminOnly: true },
      { label: "Devices", href: "/security/devices", icon: Laptop, adminOnly: true },
      { label: "Findings", href: "/security/findings", icon: Bug, adminOnly: true },
      { label: "Alerts", href: "/security/alerts", icon: AlertTriangle, adminOnly: true },
      { label: "Approvals", href: "/security/approvals", icon: ClipboardCheck, adminOnly: true },
      { label: "Policies", href: "/security/policies", icon: FileCheck, adminOnly: true },
      { label: "Vulnerable software", href: "/security/advisories", icon: ScrollText, adminOnly: true },
      { label: "Audit trail", href: "/security/audit", icon: ScrollText, adminOnly: true },
      { label: "Rules and scoring", href: "/security/rules", icon: BookOpen, adminOnly: true },
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
  security: "Endpoint security",
  devices: "Devices",
  findings: "Findings",
  alerts: "Alerts",
  approvals: "Approvals",
  policies: "Policies",
  advisories: "Vulnerable software",
  audit: "Audit trail",
  rules: "Rules and scoring",
};

/** Hrefs that are parents of other nav items must match exactly, or they would always look active. */
const exactOnly = new Set(["/security"]);

export function isActivePath(pathname: string, href: string): boolean {
  if (exactOnly.has(href)) return pathname === href;
  return pathname === href || pathname.startsWith(`${href}/`);
}

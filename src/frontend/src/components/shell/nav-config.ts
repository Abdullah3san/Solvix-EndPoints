import type { LucideIcon } from "lucide-react";
import { BookOpen, Bot, Building2, FileText, GitBranch, LayoutDashboard, Network, ScrollText, Settings2, ShieldCheck, Ticket } from "lucide-react";

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
    title: "AI assistant",
    items: [
      { label: "Assistant", href: "/assistant", icon: Bot },
      { label: "Approvals", href: "/assistant/approvals", icon: ShieldCheck },
      { label: "Knowledge base", href: "/assistant/knowledge", icon: BookOpen },
      { label: "Tickets", href: "/assistant/tickets", icon: Ticket },
      { label: "Reports", href: "/assistant/reports", icon: FileText },
      { label: "AI audit log", href: "/assistant/audit", icon: ScrollText, adminOnly: true },
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
  assistant: "AI assistant",
  approvals: "Approvals",
  knowledge: "Knowledge base",
  tickets: "Tickets",
  reports: "Reports",
  audit: "AI audit log",
};

function matches(pathname: string, href: string): boolean {
  return pathname === href || pathname.startsWith(`${href}/`);
}

/** Active when the path matches and no other, more specific navigation item also matches (e.g. /assistant vs /assistant/approvals). */
export function isActivePath(pathname: string, href: string): boolean {
  if (!matches(pathname, href)) return false;
  return !navigation.some((section) => section.items.some((item) => item.href.length > href.length && matches(pathname, item.href)));
}

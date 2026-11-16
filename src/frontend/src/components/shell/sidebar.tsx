"use client";

import Link from "next/link";
import { usePathname } from "next/navigation";
import { PanelLeftClose, PanelLeftOpen } from "lucide-react";
import { cn } from "@/lib/utils";
import { isDevelopment } from "@/lib/env";
import { visibleNavigation, isActivePath } from "./nav-config";
import { SolvixWordmark } from "./logo";
import { useCan, useSession } from "@/components/providers/session-provider";
import { Sheet, SheetContent, SheetDescription, SheetTitle } from "@/components/ui/sheet";
import { Tooltip } from "@/components/ui/tooltip";
import { initials } from "@/lib/utils";

interface SidebarContentProps {
  /** Icon-only rail (desktop). Labels move into tooltips. */
  collapsed?: boolean;
  /** Called after a link is followed (closes the mobile drawer). */
  onNavigate?: () => void;
  onToggleCollapsed?: () => void;
}

/** Brand, workspace and primary navigation. Rendered in the desktop rail and the mobile drawer. */
export function SidebarContent({ collapsed = false, onNavigate, onToggleCollapsed }: SidebarContentProps) {
  const pathname = usePathname();
  const { tenant } = useSession();
  const { isAdmin } = useCan();
  const sections = visibleNavigation({ isAdmin, isDevelopment });

  return (
    <div className="flex h-full flex-col bg-sidebar text-sidebar-foreground">
      <div className={cn("flex h-header shrink-0 items-center", collapsed ? "justify-center px-2" : "px-4")}>
        <Link href="/dashboard" onClick={onNavigate} className="rounded-md" aria-label="Solvix EndPoint home">
          <SolvixWordmark compact={collapsed} />
        </Link>
      </div>

      <div className={cn("shrink-0", collapsed ? "px-2 pb-2" : "px-3 pb-3")}>
        {collapsed ? (
          <Tooltip content={tenant.name} side="right">
            <div className="mx-auto flex size-9 items-center justify-center rounded-md bg-sidebar-active text-xs font-semibold" aria-label={`Workspace: ${tenant.name}`}>
              {initials(tenant.name)}
            </div>
          </Tooltip>
        ) : (
          <div className="flex items-center gap-2.5 rounded-md border border-sidebar-border bg-sidebar-hover px-2.5 py-2">
            <span className="flex size-8 shrink-0 items-center justify-center rounded-md bg-sidebar-active text-xs font-semibold" aria-hidden>
              {initials(tenant.name)}
            </span>
            <div className="min-w-0">
              <p className="truncate text-sm font-medium leading-5">{tenant.name}</p>
              <p className="truncate text-xs leading-4 text-sidebar-muted">{tenant.slug}</p>
            </div>
          </div>
        )}
      </div>

      <nav aria-label="Primary" className={cn("scrollbar-thin min-h-0 flex-1 overflow-y-auto pb-4", collapsed ? "px-2" : "px-3")}>
        {sections.map((section, index) => (
          <div key={section.title ?? index} className={cn(index > 0 && (collapsed ? "mt-2 border-t border-sidebar-border pt-2" : "mt-5"))}>
            {section.title && !collapsed ? (
              <p className="mb-1.5 px-2.5 text-2xs font-semibold uppercase tracking-[0.08em] text-sidebar-muted">{section.title}</p>
            ) : null}
            <ul className="space-y-0.5">
              {section.items.map((item) => {
                const active = isActivePath(pathname, item.href);
                const link = (
                  <Link
                    href={item.href}
                    onClick={onNavigate}
                    aria-current={active ? "page" : undefined}
                    aria-label={collapsed ? item.label : undefined}
                    className={cn(
                      "relative flex h-9 items-center gap-3 rounded-md text-sm transition-colors focus-visible:outline-sidebar-indicator",
                      collapsed ? "justify-center px-0" : "px-2.5",
                      active
                        ? "bg-sidebar-active font-medium text-sidebar-active-foreground"
                        : "text-sidebar-muted hover:bg-sidebar-hover hover:text-sidebar-foreground",
                    )}
                  >
                    {active ? <span className="absolute inset-y-2 left-0 w-0.5 rounded-full bg-sidebar-indicator" aria-hidden /> : null}
                    <item.icon className={cn("size-4 shrink-0", active && "text-sidebar-indicator")} aria-hidden />
                    {collapsed ? null : <span className="truncate">{item.label}</span>}
                  </Link>
                );
                return (
                  <li key={item.href}>
                    {collapsed ? (
                      <Tooltip content={item.label} side="right" delayDuration={0}>
                        {link}
                      </Tooltip>
                    ) : (
                      link
                    )}
                  </li>
                );
              })}
            </ul>
          </div>
        ))}
      </nav>

      {onToggleCollapsed ? (
        <div className={cn("shrink-0 border-t border-sidebar-border p-2", collapsed && "flex justify-center")}>
          <button
            type="button"
            onClick={onToggleCollapsed}
            aria-label={collapsed ? "Expand navigation" : "Collapse navigation"}
            aria-expanded={!collapsed}
            className={cn(
              "flex h-9 items-center gap-3 rounded-md text-sm text-sidebar-muted transition-colors hover:bg-sidebar-hover hover:text-sidebar-foreground focus-visible:outline-sidebar-indicator",
              collapsed ? "w-9 justify-center" : "w-full px-2.5",
            )}
          >
            {collapsed ? <PanelLeftOpen className="size-4" aria-hidden /> : <PanelLeftClose className="size-4" aria-hidden />}
            {collapsed ? null : <span>Collapse</span>}
          </button>
        </div>
      ) : null}
    </div>
  );
}

interface SidebarProps {
  collapsed: boolean;
  onToggleCollapsed: () => void;
  mobileOpen: boolean;
  onMobileOpenChange: (open: boolean) => void;
}

/** Desktop: a fixed rail that collapses to icons. Below `lg`: a drawer opened from the header. */
export function Sidebar({ collapsed, onToggleCollapsed, mobileOpen, onMobileOpenChange }: SidebarProps) {
  return (
    <>
      <aside
        className={cn(
          "sticky top-0 hidden h-dvh shrink-0 border-r border-sidebar-border transition-[width] duration-200 lg:block",
          collapsed ? "w-sidebar-collapsed" : "w-sidebar",
        )}
      >
        <SidebarContent collapsed={collapsed} onToggleCollapsed={onToggleCollapsed} />
      </aside>
      <Sheet open={mobileOpen} onOpenChange={onMobileOpenChange}>
        <SheetContent side="left" className="w-72 border-sidebar-border p-0 sm:max-w-72 lg:hidden [&>button]:text-sidebar-muted [&>button]:hover:bg-sidebar-hover [&>button]:hover:text-sidebar-foreground">
          <SheetTitle className="sr-only">Navigation</SheetTitle>
          <SheetDescription className="sr-only">Primary navigation for Solvix EndPoint</SheetDescription>
          <SidebarContent onNavigate={() => onMobileOpenChange(false)} />
        </SheetContent>
      </Sheet>
    </>
  );
}

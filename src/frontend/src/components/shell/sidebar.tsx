"use client";

import Link from "next/link";
import { usePathname } from "next/navigation";
import { X } from "lucide-react";
import { cn } from "@/lib/utils";
import { navigation, activeHref } from "./nav-config";
import { SolvixWordmark } from "./logo";
import { hasPermission, useSession } from "@/components/providers/session-provider";

interface SidebarProps {
  open: boolean;
  onClose: () => void;
}

export function Sidebar({ open, onClose }: SidebarProps) {
  const pathname = usePathname();
  const session = useSession();
  const { tenant } = session;
  const current = activeHref(pathname);

  return (
    <>
      {open ? <div className="fixed inset-0 z-30 bg-black/50 lg:hidden" onClick={onClose} aria-hidden /> : null}
      <aside
        aria-label="Primary navigation"
        className={cn(
          "fixed inset-y-0 left-0 z-40 flex w-64 flex-col border-r border-sidebar-border bg-sidebar text-sidebar-foreground transition-transform lg:static lg:translate-x-0",
          open ? "translate-x-0" : "-translate-x-full",
        )}
      >
        <div className="flex h-16 items-center justify-between px-5">
          <Link href="/dashboard" className="rounded-md" aria-label="Solvix home">
            <SolvixWordmark />
          </Link>
          <button type="button" onClick={onClose} className="rounded-md p-1 text-sidebar-muted hover:text-sidebar-foreground lg:hidden" aria-label="Close navigation">
            <X className="h-5 w-5" />
          </button>
        </div>

        <div className="mx-4 mb-3 rounded-md border border-sidebar-border bg-white/5 px-3 py-2">
          <p className="truncate text-sm font-medium">{tenant.name}</p>
          <p className="truncate text-xs text-sidebar-muted">{tenant.slug}</p>
        </div>

        <nav className="flex-1 overflow-y-auto px-3 pb-4">
          {navigation
            .map((section) => ({ ...section, items: section.items.filter((item) => hasPermission(session, item.permission)) }))
            .filter((section) => section.items.length > 0)
            .map((section, index) => (
            <div key={section.title ?? index} className="mb-4">
              {section.title ? (
                <p className="mb-1 px-2 text-[11px] font-semibold uppercase tracking-wider text-sidebar-muted">{section.title}</p>
              ) : null}
              <ul className="space-y-0.5">
                {section.items
                  .map((item) => {
                    const active = item.href === current;
                    return (
                      <li key={item.href}>
                        <Link
                          href={item.href}
                          onClick={onClose}
                          aria-current={active ? "page" : undefined}
                          className={cn(
                            "flex items-center gap-3 rounded-md px-2.5 py-2 text-sm transition-colors",
                            active
                              ? "bg-sidebar-active text-sidebar-active-foreground font-medium"
                              : "text-sidebar-muted hover:bg-white/5 hover:text-sidebar-foreground",
                          )}
                        >
                          <item.icon className="h-4 w-4 shrink-0" aria-hidden />
                          <span className="truncate">{item.label}</span>
                        </Link>
                      </li>
                    );
                  })}
              </ul>
            </div>
          ))}
        </nav>

        <div className="border-t border-sidebar-border px-5 py-3 text-xs text-sidebar-muted">Solvix EndPoints</div>
      </aside>
    </>
  );
}

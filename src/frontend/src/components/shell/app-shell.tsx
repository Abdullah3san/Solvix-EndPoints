"use client";

import * as React from "react";
import { Sidebar } from "./sidebar";
import { Header } from "./header";

const STORAGE_KEY = "solvix.sidebar.collapsed";
const listeners = new Set<() => void>();

const collapsedStore = {
  subscribe(listener: () => void) {
    listeners.add(listener);
    return () => listeners.delete(listener);
  },
  get(): boolean {
    try {
      return localStorage.getItem(STORAGE_KEY) === "1";
    } catch {
      return false;
    }
  },
  set(value: boolean) {
    try {
      localStorage.setItem(STORAGE_KEY, value ? "1" : "0");
    } catch {
      // storage unavailable: the preference lasts until reload
    }
    for (const listener of listeners) listener();
  },
};

/** Application frame: sidebar + sticky header + page content. */
export function AppShell({ children }: { children: React.ReactNode }) {
  const [mobileOpen, setMobileOpen] = React.useState(false);
  const collapsed = React.useSyncExternalStore(collapsedStore.subscribe, collapsedStore.get, () => false);

  return (
    <div className="flex min-h-dvh bg-background">
      <a
        href="#main"
        className="sr-only focus:not-sr-only focus:fixed focus:left-3 focus:top-3 focus:z-[80] focus:rounded-md focus:bg-primary focus:px-3 focus:py-2 focus:text-sm focus:font-medium focus:text-primary-foreground"
      >
        Skip to content
      </a>
      <Sidebar collapsed={collapsed} onToggleCollapsed={() => collapsedStore.set(!collapsed)} mobileOpen={mobileOpen} onMobileOpenChange={setMobileOpen} />
      <div className="flex min-w-0 flex-1 flex-col">
        <Header onOpenSidebar={() => setMobileOpen(true)} />
        <main id="main" tabIndex={-1} className="flex-1 px-4 py-6 outline-none sm:px-6 lg:px-8">
          <div className="mx-auto w-full max-w-content">{children}</div>
        </main>
      </div>
    </div>
  );
}

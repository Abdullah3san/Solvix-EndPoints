"use client";

import * as React from "react";
import { Sidebar } from "./sidebar";
import { Header } from "./header";

export function AppShell({ children }: { children: React.ReactNode }) {
  const [sidebarOpen, setSidebarOpen] = React.useState(false);
  const close = React.useCallback(() => setSidebarOpen(false), []);

  return (
    <div className="flex min-h-dvh bg-background">
      <Sidebar open={sidebarOpen} onClose={close} />
      <div className="flex min-w-0 flex-1 flex-col">
        <Header onOpenSidebar={() => setSidebarOpen(true)} />
        <main id="main" className="flex-1 px-4 py-6 sm:px-6 lg:px-8">
          <div className="mx-auto w-full max-w-6xl">{children}</div>
        </main>
      </div>
    </div>
  );
}

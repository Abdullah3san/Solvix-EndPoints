"use client";

import { Menu as MenuIcon } from "lucide-react";
import { Breadcrumbs } from "./breadcrumbs";
import { GlobalSearch } from "./global-search";
import { ThemeToggle } from "./theme-toggle";
import { NotificationsMenu } from "./notifications-menu";
import { UserMenu } from "./user-menu";
import { Button } from "@/components/ui/button";

export function Header({ onOpenSidebar }: { onOpenSidebar: () => void }) {
  return (
    <header className="sticky top-0 z-30 flex h-header items-center gap-2 border-b border-border bg-background/85 px-4 backdrop-blur supports-[backdrop-filter]:bg-background/70 sm:gap-3 sm:px-6 lg:px-8">
      <Button variant="ghost" size="icon" onClick={onOpenSidebar} className="-ml-2 text-muted-foreground lg:hidden" aria-label="Open navigation">
        <MenuIcon className="size-5" />
      </Button>
      <div className="min-w-0 flex-1">
        <Breadcrumbs />
      </div>
      <GlobalSearch />
      <div className="flex items-center gap-0.5">
        <ThemeToggle />
        <NotificationsMenu />
        <div className="mx-1.5 hidden h-6 w-px bg-border sm:block" aria-hidden />
        <UserMenu />
      </div>
    </header>
  );
}

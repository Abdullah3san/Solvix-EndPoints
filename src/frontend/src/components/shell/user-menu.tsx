"use client";

import * as React from "react";
import { useRouter } from "next/navigation";
import { ChevronDown, LogOut, Settings2, UserRound } from "lucide-react";
import Link from "next/link";
import { Menu, MenuItem, MenuLabel, MenuPanel, MenuSeparator, MenuTrigger } from "@/components/ui/menu";
import { Badge } from "@/components/ui/badge";
import { useSession } from "@/components/providers/session-provider";
import { initials } from "@/lib/utils";

export function UserMenu() {
  const { user, tenant } = useSession();
  const router = useRouter();
  const [signingOut, setSigningOut] = React.useState(false);

  async function signOut() {
    setSigningOut(true);
    try {
      await fetch("/api/auth/logout", { method: "POST" });
    } finally {
      router.replace("/login");
      router.refresh();
    }
  }

  return (
    <Menu>
      <MenuTrigger className="h-9 gap-2 rounded-md px-1.5 hover:bg-muted" aria-label="Account menu">
        <span className="flex h-7 w-7 items-center justify-center rounded-full bg-primary text-xs font-semibold text-primary-foreground">
          {initials(user.displayName)}
        </span>
        <span className="hidden max-w-32 truncate text-sm font-medium md:inline">{user.displayName}</span>
        <ChevronDown className="hidden h-4 w-4 text-muted-foreground md:inline" />
      </MenuTrigger>
      <MenuPanel label="Account" className="w-64">
        <div className="px-2.5 py-2">
          <p className="truncate text-sm font-medium">{user.displayName}</p>
          <p className="truncate text-xs text-muted-foreground">{user.email}</p>
          <div className="mt-2 flex items-center gap-2 text-xs text-muted-foreground">
            <span className="truncate">{tenant.name}</span>
            <Badge tone="brand">{tenant.role}</Badge>
          </div>
        </div>
        <MenuSeparator />
        <MenuLabel>Workspace</MenuLabel>
        <Link href="/settings/workspace" className="block">
          <MenuItem>
            <Settings2 className="h-4 w-4" /> Workspace settings
          </MenuItem>
        </Link>
        <MenuItem disabled>
          <UserRound className="h-4 w-4" /> Profile (coming soon)
        </MenuItem>
        <MenuSeparator />
        <MenuItem tone="danger" onSelect={signOut} disabled={signingOut}>
          <LogOut className="h-4 w-4" /> Sign out
        </MenuItem>
      </MenuPanel>
    </Menu>
  );
}

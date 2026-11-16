"use client";

import * as React from "react";
import Link from "next/link";
import { useRouter } from "next/navigation";
import { ChevronDown, LogOut, Settings2 } from "lucide-react";
import { Avatar } from "@/components/ui/avatar";
import { Badge } from "@/components/ui/badge";
import { DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuSeparator, DropdownMenuTrigger } from "@/components/ui/dropdown-menu";
import { useSession } from "@/components/providers/session-provider";

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
    <DropdownMenu>
      <DropdownMenuTrigger className="flex h-9 items-center gap-2 rounded-md px-1.5 hover:bg-muted" aria-label="Account menu">
        <Avatar name={user.displayName} size="md" className="size-7 bg-primary text-primary-foreground" />
        <span className="hidden max-w-32 truncate text-sm font-medium md:inline">{user.displayName}</span>
        <ChevronDown className="hidden size-4 text-muted-foreground md:inline" aria-hidden />
      </DropdownMenuTrigger>
      <DropdownMenuContent className="w-64">
        <div className="px-2.5 py-2">
          <p className="truncate text-sm font-medium">{user.displayName}</p>
          <p className="truncate text-xs text-muted-foreground">{user.email}</p>
          <div className="mt-2 flex items-center gap-2 text-xs text-muted-foreground">
            <span className="truncate">{tenant.name}</span>
            <Badge tone="brand">{tenant.role}</Badge>
          </div>
        </div>
        <DropdownMenuSeparator />
        <DropdownMenuItem asChild>
          <Link href="/settings/workspace">
            <Settings2 /> Workspace settings
          </Link>
        </DropdownMenuItem>
        <DropdownMenuSeparator />
        <DropdownMenuItem tone="danger" onSelect={signOut} disabled={signingOut}>
          <LogOut /> Sign out
        </DropdownMenuItem>
      </DropdownMenuContent>
    </DropdownMenu>
  );
}

"use client";

import * as React from "react";
import { useRouter } from "next/navigation";
import { ArrowLeftRight, ChevronDown, KeyRound, LogOut, Settings2 } from "lucide-react";
import Link from "next/link";
import { Menu, MenuItem, MenuLabel, MenuPanel, MenuSeparator, MenuTrigger } from "@/components/ui/menu";
import { Badge } from "@/components/ui/badge";
import { useSession } from "@/components/providers/session-provider";
import { useToast } from "@/components/providers/toast-provider";
import { api, authPost } from "@/lib/api/client";
import type { AuthTenantDto } from "@/lib/api/types";
import { messageFrom } from "@/lib/forms";
import { initials } from "@/lib/utils";

export function UserMenu() {
  const { user, tenant, roles } = useSession();
  const router = useRouter();
  const { toast } = useToast();
  const [signingOut, setSigningOut] = React.useState(false);
  const [workspaces, setWorkspaces] = React.useState<AuthTenantDto[] | null>(null);

  React.useEffect(() => {
    let cancelled = false;
    api
      .get<AuthTenantDto[]>("auth/workspaces")
      .then((list) => !cancelled && setWorkspaces(list))
      .catch(() => !cancelled && setWorkspaces([]));
    return () => {
      cancelled = true;
    };
  }, []);

  async function signOut() {
    setSigningOut(true);
    try {
      // Ends the session on the server, not just in this browser.
      await authPost("/api/auth/logout");
    } catch {
      // The cookies are cleared either way.
    } finally {
      router.replace("/login");
      router.refresh();
    }
  }

  async function switchTo(slug: string) {
    try {
      await authPost("/api/auth/switch-tenant", { tenantSlug: slug });
      router.replace("/dashboard");
      router.refresh();
    } catch (e) {
      toast({ tone: "error", title: "Could not switch workspace", description: messageFrom(e) });
    }
  }

  const others = (workspaces ?? []).filter((w) => w.id !== tenant.id);

  return (
    <Menu>
      <MenuTrigger className="h-9 gap-2 rounded-md px-1.5 hover:bg-muted" aria-label="Account menu">
        <span className="flex h-7 w-7 items-center justify-center rounded-full bg-primary text-xs font-semibold text-primary-foreground">
          {initials(user.displayName)}
        </span>
        <span className="hidden max-w-32 truncate text-sm font-medium md:inline">{user.displayName}</span>
        <ChevronDown className="hidden h-4 w-4 text-muted-foreground md:inline" />
      </MenuTrigger>
      <MenuPanel label="Account" className="w-72">
        <div className="px-2.5 py-2">
          <p className="truncate text-sm font-medium">{user.displayName}</p>
          <p className="truncate text-xs text-muted-foreground">{user.email}</p>
          <p className="mt-2 truncate text-xs text-muted-foreground">{tenant.name}</p>
          <div className="mt-1 flex flex-wrap gap-1">
            {roles.map((role) => (
              <Badge key={role} tone="brand">
                {role}
              </Badge>
            ))}
          </div>
        </div>
        <MenuSeparator />
        <Link href="/account/security" className="block">
          <MenuItem>
            <KeyRound className="h-4 w-4" /> Password, MFA &amp; sessions
          </MenuItem>
        </Link>
        <Link href="/settings/workspace" className="block">
          <MenuItem>
            <Settings2 className="h-4 w-4" /> Workspace settings
          </MenuItem>
        </Link>
        {others.length > 0 ? (
          <>
            <MenuSeparator />
            <MenuLabel>Switch workspace</MenuLabel>
            {others.map((w) => (
              <MenuItem key={w.id} onSelect={() => switchTo(w.slug)}>
                <ArrowLeftRight className="h-4 w-4" /> <span className="truncate">{w.name}</span>
              </MenuItem>
            ))}
          </>
        ) : null}
        <MenuSeparator />
        <MenuItem tone="danger" onSelect={signOut} disabled={signingOut}>
          <LogOut className="h-4 w-4" /> Sign out
        </MenuItem>
      </MenuPanel>
    </Menu>
  );
}

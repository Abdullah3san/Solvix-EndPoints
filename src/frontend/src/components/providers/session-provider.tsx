"use client";

import * as React from "react";
import type { AuthTenantDto, UserDto } from "@/lib/api/types";

export interface Session {
  user: UserDto;
  tenant: AuthTenantDto;
}

const SessionContext = React.createContext<Session | null>(null);

export function SessionProvider({ session, children }: { session: Session; children: React.ReactNode }) {
  return <SessionContext.Provider value={session}>{children}</SessionContext.Provider>;
}

export function useSession(): Session {
  const session = React.useContext(SessionContext);
  if (!session) throw new Error("useSession must be used inside <SessionProvider>");
  return session;
}

export function useCan() {
  const { tenant } = useSession();
  const isOwner = tenant.role === "Owner";
  const isAdmin = isOwner || tenant.role === "Admin";
  return { isOwner, isAdmin, role: tenant.role };
}

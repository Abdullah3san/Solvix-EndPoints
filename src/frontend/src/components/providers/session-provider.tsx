"use client";

import * as React from "react";
import type { CurrentUserDto, Permission } from "@/lib/api/types";

export type Session = CurrentUserDto;

const SessionContext = React.createContext<Session | null>(null);

export function SessionProvider({ session, children }: { session: Session; children: React.ReactNode }) {
  return <SessionContext.Provider value={session}>{children}</SessionContext.Provider>;
}

export function useSession(): Session {
  const session = React.useContext(SessionContext);
  if (!session) throw new Error("useSession must be used inside <SessionProvider>");
  return session;
}

/**
 * Whether the signed-in user holds a permission in the current workspace.
 * Used ONLY to hide controls the user cannot use. Every request is authorized
 * again by the API; hiding a button is a courtesy, not a security boundary.
 * While the workspace requires MFA and this session has none, nothing is usable.
 */
export function useCan(permission: Permission): boolean {
  const session = useSession();
  return !session.mfaEnrollmentRequired && session.permissions.includes(permission);
}

export function hasPermission(session: Pick<Session, "permissions" | "mfaEnrollmentRequired">, permission: Permission | undefined): boolean {
  return permission === undefined || (!session.mfaEnrollmentRequired && session.permissions.includes(permission));
}

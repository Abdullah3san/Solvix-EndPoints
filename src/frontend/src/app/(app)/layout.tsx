import { redirect } from "next/navigation";
import { serverApi } from "@/lib/api/server";
import { ApiError } from "@/lib/api/errors";
import type { CurrentUserDto } from "@/lib/api/types";
import { AccessBlocked } from "@/components/features/access-blocked";
import { SessionProvider } from "@/components/providers/session-provider";
import { NotificationsProvider } from "@/components/providers/notifications-provider";
import { AppShell } from "@/components/shell/app-shell";

export const dynamic = "force-dynamic";

/**
 * Authenticated area. The session is validated against the API on every
 * navigation (cheap: tenant + membership are cached server-side), so a
 * revoked membership or suspended tenant is reflected immediately.
 */
export default async function AppLayout({ children }: { children: React.ReactNode }) {
  let session: CurrentUserDto;
  try {
    session = await serverApi<CurrentUserDto>("auth/me", { redirectOnUnauthorized: false });
  } catch (error) {
    if (error instanceof ApiError && error.status === 403) {
      return <AccessBlocked suspended={error.code === "tenant_inactive"} detail={error.problem.detail} />;
    }
    if (error instanceof ApiError && error.status !== 401) throw error; // outage: show the error page, not the sign-in page
    redirect("/api/auth/expired");
  }

  return (
    <SessionProvider session={session}>
      <NotificationsProvider>
        <AppShell>{children}</AppShell>
      </NotificationsProvider>
    </SessionProvider>
  );
}

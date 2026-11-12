import { redirect } from "next/navigation";
import { serverApi } from "@/lib/api/server";
import type { CurrentUserDto } from "@/lib/api/types";
import { SessionProvider } from "@/components/providers/session-provider";
import { NotificationsProvider } from "@/components/providers/notifications-provider";
import { RealtimeProvider } from "@/components/providers/realtime-provider";
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
    session = await serverApi<CurrentUserDto>("auth/me");
  } catch {
    redirect("/login?reason=expired");
  }

  return (
    <SessionProvider session={session}>
      <NotificationsProvider>
        <RealtimeProvider>
          <AppShell>{children}</AppShell>
        </RealtimeProvider>
      </NotificationsProvider>
    </SessionProvider>
  );
}

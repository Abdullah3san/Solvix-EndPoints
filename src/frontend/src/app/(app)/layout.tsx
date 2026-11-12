import Link from "next/link";
import { redirect } from "next/navigation";
import { serverApi } from "@/lib/api/server";
import type { CurrentUserDto } from "@/lib/api/types";
import { SessionProvider } from "@/components/providers/session-provider";
import { NotificationsProvider } from "@/components/providers/notifications-provider";
import { AppShell } from "@/components/shell/app-shell";
import { Alert } from "@/components/ui/alert";

export const dynamic = "force-dynamic";

/**
 * Authenticated area. The session is validated against the API on every
 * navigation (cheap: session, membership and permissions are cached
 * server-side), so a revoked session, a removed member or a changed role is
 * reflected immediately.
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
        <AppShell>
          {session.mfaEnrollmentRequired ? (
            <Alert tone="error" title="Multi-factor authentication required" className="mb-6">
              This workspace requires a second sign-in factor. Until you set one up, you can only manage your own account.{" "}
              <Link href="/account/security" className="font-medium text-primary hover:underline">
                Set up MFA now
              </Link>
            </Alert>
          ) : null}
          {children}
        </AppShell>
      </NotificationsProvider>
    </SessionProvider>
  );
}

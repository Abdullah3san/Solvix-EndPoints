import { notFound } from "next/navigation";
import { serverApi } from "@/lib/api/server";
import type { CurrentUserDto } from "@/lib/api/types";

export const dynamic = "force-dynamic";

/**
 * The provider console exists only for the platform operator; for everyone else
 * it does not exist (404, before anything streams). The API enforces the same
 * rule on every endpoint and answers 403.
 */
export default async function PlatformLayout({ children }: { children: React.ReactNode }) {
  const me = await serverApi<CurrentUserDto>("auth/me");
  if (me.user.isPlatformAdmin !== true) notFound();
  return children;
}

"use client";

import * as React from "react";
import { useRouter } from "next/navigation";
import { LogOut, ShieldAlert } from "lucide-react";
import { SolvixWordmark } from "@/components/shell/logo";
import { Button } from "@/components/ui/button";

/**
 * Shown instead of the app when the API refuses the session with 403: the
 * workspace was suspended by the provider, or the user's membership was removed.
 * Offers sign-out only; there is nothing else the user can do from here.
 */
export function AccessBlocked({ suspended, detail }: { suspended: boolean; detail?: string }) {
  const router = useRouter();
  const [pending, setPending] = React.useState(false);

  async function signOut() {
    setPending(true);
    try {
      await fetch("/api/auth/logout", { method: "POST" });
    } finally {
      router.replace("/login");
      router.refresh();
    }
  }

  return (
    <main className="flex min-h-dvh flex-col items-center justify-center gap-6 bg-background p-6 text-center">
      <SolvixWordmark className="text-foreground" />
      <div className="flex max-w-md flex-col items-center gap-3">
        <span className="flex size-12 items-center justify-center rounded-full bg-warning/14 text-warning">
          <ShieldAlert className="size-6" aria-hidden />
        </span>
        <h1 className="text-xl font-semibold">{suspended ? "This workspace is suspended" : "You no longer have access"}</h1>
        <p className="text-sm text-muted-foreground">
          {suspended
            ? "Access to this workspace has been paused by your service provider. Your data is safe. Contact them to restore access."
            : (detail ?? "Your access to this workspace was removed. Contact your workspace administrator.")}
        </p>
      </div>
      <Button variant="outline" onClick={signOut} loading={pending}>
        {pending ? null : <LogOut />} Sign out
      </Button>
    </main>
  );
}

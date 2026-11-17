"use client";

import * as React from "react";
import Link from "next/link";
import { Download } from "lucide-react";
import { api } from "@/lib/api/client";
import { installerUrl, type DeploymentProfileDto } from "@/lib/api/types";
import { messageFrom } from "@/lib/forms";
import { Alert } from "@/components/ui/alert";
import { Badge } from "@/components/ui/badge";
import { Button, buttonClasses } from "@/components/ui/button";
import { Dialog } from "@/components/ui/dialog";
import { hasPermission, useSession } from "@/components/providers/session-provider";

/**
 * Top-bar "Download agent": one click gives the Windows installer (.exe). The installer is generic; computers that
 * run it wait in the approval queue (Endpoints > Enrollment) until an administrator accepts them.
 */
export function DownloadAgentButton() {
  const session = useSession();
  const [open, setOpen] = React.useState(false);
  const [profiles, setProfiles] = React.useState<DeploymentProfileDto[] | null>(null);
  const [error, setError] = React.useState<string | null>(null);

  if (!hasPermission(session, "devices.enroll")) return null;

  async function show() {
    setOpen(true);
    setError(null);
    try {
      setProfiles(await api.get<DeploymentProfileDto[]>("deployment-profiles"));
    } catch (e) {
      setError(messageFrom(e));
    }
  }

  const enabled = profiles?.filter((p) => p.isEnabled) ?? [];

  return (
    <>
      <Button variant="outline" size="sm" onClick={show} aria-label="Download agent">
        <Download className="h-4 w-4" />
        <span className="hidden sm:inline">Download agent</span>
      </Button>
      {open ? (
        <Dialog
          open
          onClose={() => setOpen(false)}
          title="Download the Solvix agent"
          description="One Windows installer (.exe). Run it on a computer and approve the administrator prompt; it appears under Pending approval until you accept it."
          className="max-w-xl"
        >
          <div className="space-y-3">
            {error ? <Alert tone="error">{error}</Alert> : null}
            {profiles === null && !error ? <p className="text-sm text-muted-foreground">Loading…</p> : null}
            {profiles !== null && enabled.length === 0 ? <Alert>No enabled deployment profile. Enable one under Endpoints → Enrollment.</Alert> : null}
            <ul className="space-y-2">
              {enabled.map((p) => {
                const placement = [p.branchName, p.departmentName, p.deviceGroupName].filter(Boolean).join(" / ");
                return (
                  <li key={p.id} className="flex items-center justify-between gap-3 rounded-md border border-border p-3">
                    <div className="min-w-0">
                      <p className="truncate text-sm font-medium">
                        {p.name} {p.pendingCount > 0 ? <Badge tone="warning">{p.pendingCount} waiting</Badge> : null}
                      </p>
                      <p className="truncate text-xs text-muted-foreground">{placement || "No branch or group"}</p>
                    </div>
                    <a href={installerUrl(p.id)} download className={buttonClasses({ size: "sm" })}>
                      <Download className="h-4 w-4" /> Download .exe
                    </a>
                  </li>
                );
              })}
            </ul>
            <p className="text-xs text-muted-foreground">
              Manage profiles, approvals and settings under <Link className="underline" href="/endpoints/enrollment" onClick={() => setOpen(false)}>Endpoints → Enrollment</Link>.
            </p>
          </div>
        </Dialog>
      ) : null}
    </>
  );
}

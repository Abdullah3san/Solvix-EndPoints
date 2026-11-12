import type { Metadata } from "next";
import { serverApiOrForbidden } from "@/lib/api/server";
import type { AuditEventDto, PagedResult } from "@/lib/api/types";
import { PageHeader } from "@/components/ui/page-header";
import { NoAccess } from "@/components/features/no-access";
import { AuditLogView } from "@/components/features/audit-log-view";

export const metadata: Metadata = { title: "Audit log" };

export default async function AuditPage() {
  const events = await serverApiOrForbidden<PagedResult<AuditEventDto>>("security/audit-events?pageSize=50");
  return (
    <>
      <PageHeader title="Audit log" description="Sign-ins, sessions and access changes in this workspace. Entries cannot be edited or deleted." />
      {events ? <AuditLogView initial={events} /> : <NoAccess what="the audit log" />}
    </>
  );
}

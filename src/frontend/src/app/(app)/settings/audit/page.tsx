import type { Metadata } from "next";
import { serverApi } from "@/lib/api/server";
import type { PagedResult } from "@/lib/api/types";
import type { AuditEntryDto } from "@/lib/api/endpoint-types";
import { PageHeader } from "@/components/ui/page-header";
import { AuditView } from "@/components/features/endpoints/audit-view";

export const metadata: Metadata = { title: "Audit log" };

export default async function AuditPage() {
  const entries = await serverApi<PagedResult<AuditEntryDto>>("audit?pageSize=50");
  return (
    <>
      <PageHeader title="Audit log" description="Append-only history of administrative, agent and system actions." />
      <AuditView initial={entries} />
    </>
  );
}

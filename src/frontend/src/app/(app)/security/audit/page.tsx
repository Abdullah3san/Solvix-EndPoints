import type { Metadata } from "next";
import { PageHeader } from "@/components/ui/page-header";
import { AuditView } from "@/components/features/security/audit";

export const metadata: Metadata = { title: "Audit trail" };

export default function Page() {
  return (
    <>
      <PageHeader title="Audit trail" description="Who did what, and when. Entries cannot be edited or deleted." />
      <AuditView />
    </>
  );
}

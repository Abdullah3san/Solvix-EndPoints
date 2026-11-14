import type { Metadata } from "next";
import { PageHeader } from "@/components/ui/page-header";
import { AuditView } from "@/components/features/assistant/list-views";

export const metadata: Metadata = { title: "AI audit log" };

export default function Page() {
  return (
    <>
      <PageHeader title="AI audit log" description="Every AI request, tool call, proposal, approval and execution outcome." />
      <AuditView />
    </>
  );
}

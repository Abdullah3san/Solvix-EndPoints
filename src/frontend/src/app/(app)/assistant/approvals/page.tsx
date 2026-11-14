import type { Metadata } from "next";
import { PageHeader } from "@/components/ui/page-header";
import { ApprovalsView } from "@/components/features/assistant/list-views";

export const metadata: Metadata = { title: "Approvals" };

export default function Page() {
  return (
    <>
      <PageHeader title="Approvals" description="Actions proposed by the assistant. Nothing runs until an authorised person approves it." />
      <ApprovalsView />
    </>
  );
}

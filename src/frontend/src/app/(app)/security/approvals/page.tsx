import type { Metadata } from "next";
import { PageHeader } from "@/components/ui/page-header";
import { ApprovalsView } from "@/components/features/security/approvals";

export const metadata: Metadata = { title: "Approvals" };

export default function Page() {
  return (
    <>
      <PageHeader title="Approvals" description="Policies and device changes waiting for a second person." />
      <ApprovalsView />
    </>
  );
}

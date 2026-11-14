import type { Metadata } from "next";
import { PageHeader } from "@/components/ui/page-header";
import { ReportsView } from "@/components/features/assistant/list-views";

export const metadata: Metadata = { title: "Reports" };

export default function Page() {
  return (
    <>
      <PageHeader title="Reports" description="AI-assisted reports built from live fleet data." />
      <ReportsView />
    </>
  );
}

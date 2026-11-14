import type { Metadata } from "next";
import { PageHeader } from "@/components/ui/page-header";
import { KnowledgeView } from "@/components/features/assistant/list-views";

export const metadata: Metadata = { title: "Knowledge base" };

export default function Page() {
  return (
    <>
      <PageHeader title="Knowledge base" description="Company documents the assistant can search and cite. Private to your workspace." />
      <KnowledgeView />
    </>
  );
}

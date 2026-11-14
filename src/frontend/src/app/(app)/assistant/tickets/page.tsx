import type { Metadata } from "next";
import { PageHeader } from "@/components/ui/page-header";
import { TicketsView } from "@/components/features/assistant/list-views";

export const metadata: Metadata = { title: "Tickets" };

export default function Page() {
  return (
    <>
      <PageHeader title="Tickets" description="Issues created by people and by the assistant." />
      <TicketsView />
    </>
  );
}

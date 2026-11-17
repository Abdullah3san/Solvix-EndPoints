import type { Metadata } from "next";
import { serverApi } from "@/lib/api/server";
import type { PagedResult, TicketSummaryDto } from "@/lib/api/types";
import { PageHeader } from "@/components/ui/page-header";
import { TicketsView } from "@/components/features/support/tickets-view";

export const metadata: Metadata = { title: "Tickets" };

export default async function TicketsPage() {
  const tickets = await serverApi<PagedResult<TicketSummaryDto>>("tickets?open=true&pageSize=100");
  return (
    <>
      <PageHeader title="Tickets" description="Problems reported by people on their computers (through the agent) and by your team." />
      <TicketsView initial={tickets} />
    </>
  );
}

import type { Metadata } from "next";
import { serverApi } from "@/lib/api/server";
import { ApiError } from "@/lib/api/errors";
import type { PagedResult, TicketCategoryDto, TicketStatsDto, TicketSummaryDto } from "@/lib/api/types";
import { PageHeader } from "@/components/ui/page-header";
import { TicketsView } from "@/components/features/helpdesk/tickets-view";

export const metadata: Metadata = { title: "Tickets" };

export default async function TicketsPage() {
  const [tickets, categories] = await Promise.all([
    serverApi<PagedResult<TicketSummaryDto>>("helpdesk/tickets?open=true&pageSize=25"),
    serverApi<TicketCategoryDto[]>("helpdesk/categories"),
  ]);
  // Statistics are staff-only; the API answers 403 to plain members, which simply means "no queues".
  let stats: TicketStatsDto | null = null;
  try {
    stats = await serverApi<TicketStatsDto>("helpdesk/tickets/stats", { redirectOnUnauthorized: false });
  } catch (e) {
    if (!(e instanceof ApiError) || e.status !== 403) throw e;
  }
  return (
    <>
      <PageHeader title="Tickets" description="Requests for help from your team, by web or e-mail." />
      <TicketsView initial={tickets} stats={stats} categories={categories} isStaff={stats !== null} />
    </>
  );
}

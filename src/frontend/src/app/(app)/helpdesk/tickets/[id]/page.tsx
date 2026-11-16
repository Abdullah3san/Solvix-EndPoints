import type { Metadata } from "next";
import { notFound } from "next/navigation";
import { serverApi } from "@/lib/api/server";
import { ApiError } from "@/lib/api/errors";
import type { TechnicianDto, TicketCategoryDto, TicketDetailsDto, TicketEventDto, TicketStatsDto } from "@/lib/api/types";
import { TicketDetailsView } from "@/components/features/helpdesk/ticket-details-view";

export const metadata: Metadata = { title: "Ticket" };

export default async function TicketPage({ params }: { params: Promise<{ id: string }> }) {
  const { id } = await params;
  if (!/^[0-9a-f-]{36}$/i.test(id)) notFound();
  let ticket: TicketDetailsDto;
  try {
    ticket = await serverApi<TicketDetailsDto>(`helpdesk/tickets/${id}`);
  } catch (e) {
    if (e instanceof ApiError && e.status === 404) notFound();
    throw e;
  }
  const [history, categories, technicians] = await Promise.all([
    serverApi<TicketEventDto[]>(`helpdesk/tickets/${id}/history`),
    serverApi<TicketCategoryDto[]>("helpdesk/categories"),
    serverApi<TechnicianDto[]>("helpdesk/technicians"),
  ]);
  let isStaff = true;
  try {
    await serverApi<TicketStatsDto>("helpdesk/tickets/stats", { redirectOnUnauthorized: false });
  } catch (e) {
    if (!(e instanceof ApiError) || e.status !== 403) throw e;
    isStaff = false;
  }
  return <TicketDetailsView initial={ticket} history={history} categories={categories} technicians={isStaff ? technicians : []} isStaff={isStaff} isRequester={!isStaff} />;
}

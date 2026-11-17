import type { Metadata } from "next";
import { notFound } from "next/navigation";
import { serverApi } from "@/lib/api/server";
import { ApiError } from "@/lib/api/errors";
import type { MemberDto, PagedResult, TicketDetailsDto } from "@/lib/api/types";
import { TicketDetailsView } from "@/components/features/support/ticket-details-view";

export const metadata: Metadata = { title: "Ticket" };

export default async function TicketPage({ params }: { params: Promise<{ id: string }> }) {
  const { id } = await params;
  if (!/^[0-9a-f-]{36}$/i.test(id)) notFound();
  let ticket: TicketDetailsDto;
  try {
    ticket = await serverApi<TicketDetailsDto>(`tickets/${id}`);
  } catch (e) {
    if (e instanceof ApiError && e.status === 404) notFound();
    throw e;
  }
  const members = await serverApi<PagedResult<MemberDto>>("tenant/members?pageSize=200");
  return <TicketDetailsView initial={ticket} members={members.items} />;
}

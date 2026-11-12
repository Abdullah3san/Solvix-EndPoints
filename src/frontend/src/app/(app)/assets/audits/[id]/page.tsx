import type { Metadata } from "next";
import { notFound } from "next/navigation";
import { serverApi } from "@/lib/api/server";
import { adminOr404 } from "@/lib/api/require-admin";
import type { PagedResult, PhysicalAuditDto, PhysicalAuditItemDto } from "@/lib/api/types";
import { AuditSession } from "@/components/features/assets/audits-view";

export const metadata: Metadata = { title: "Physical audit" };

export default async function AuditPage({ params }: { params: Promise<{ id: string }> }) {
  const { id } = await params;
  if (!/^[0-9a-f-]{36}$/i.test(id)) notFound();
  const [audit, items] = await Promise.all([
    adminOr404(serverApi<PhysicalAuditDto>(`asset-audits/${id}`)),
    adminOr404(serverApi<PagedResult<PhysicalAuditItemDto>>(`asset-audits/${id}/items?pageSize=100`)),
  ]);
  return <AuditSession initial={audit} initialItems={items} />;
}

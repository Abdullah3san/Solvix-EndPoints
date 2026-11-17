import type { Metadata } from "next";
import { redirect } from "next/navigation";
import { serverApi, serverApiOrForbidden } from "@/lib/api/server";
import { ApiError } from "@/lib/api/errors";
import type { EscalationRuleDto, InboundChannelDto, TenantUserDto, PagedResult, SlaPolicyDto, TechnicianDto, TicketCategoryDto } from "@/lib/api/types";
import { PageHeader } from "@/components/ui/page-header";
import { HelpdeskSettingsView } from "@/components/features/helpdesk/helpdesk-settings-view";

export const metadata: Metadata = { title: "Helpdesk settings" };

export default async function HelpdeskSettingsPage() {
  let data;
  try {
    data = await Promise.all([
      serverApi<TicketCategoryDto[]>("helpdesk/categories?includeInactive=true"),
      serverApi<TechnicianDto[]>("helpdesk/technicians"),
      serverApi<SlaPolicyDto[]>("helpdesk/sla-policies", { redirectOnUnauthorized: false }),
      serverApi<EscalationRuleDto[]>("helpdesk/escalation-rules", { redirectOnUnauthorized: false }),
      serverApi<InboundChannelDto[]>("helpdesk/email-channels", { redirectOnUnauthorized: false }),
      serverApiOrForbidden<PagedResult<TenantUserDto>>("users?pageSize=200"),
    ]);
  } catch (e) {
    // These settings are for workspace admins; the API answers 403 to everyone else.
    if (e instanceof ApiError && e.status === 403) redirect("/helpdesk/tickets");
    throw e;
  }
  const [categories, technicians, policies, rules, channels, members] = data;
  return (
    <>
      <PageHeader title="Helpdesk settings" description="Categories, technicians, SLA policies, escalation and e-mail intake." />
      <HelpdeskSettingsView categories={categories} technicians={technicians} policies={policies} rules={rules} channels={channels} members={(members?.items ?? []).filter((m) => m.isActive)} />
    </>
  );
}

import type { Metadata } from "next";
import { notFound } from "next/navigation";
import { serverApi } from "@/lib/api/server";
import type { CurrentUserDto, PagedResult, PlatformSummaryDto, PlatformTenantDto } from "@/lib/api/types";
import { PageHeader } from "@/components/ui/page-header";
import { CompaniesView } from "@/components/features/platform/companies-view";

export const metadata: Metadata = { title: "Companies" };

/**
 * Provider console. Only the platform operator may see it: everyone else gets a
 * 404 (the API enforces the same rule independently and answers 403).
 */
export default async function CompaniesPage() {
  const me = await serverApi<CurrentUserDto>("auth/me");
  if (me.user.isPlatformAdmin !== true) notFound();

  const [summary, companies] = await Promise.all([
    serverApi<PlatformSummaryDto>("platform/summary"),
    serverApi<PagedResult<PlatformTenantDto>>("platform/tenants?pageSize=25"),
  ]);
  return (
    <>
      <PageHeader title="Companies" description="The companies subscribed to Solvix EndPoint. Only you can create, suspend or reactivate them." />
      <CompaniesView initial={companies} summary={summary} />
    </>
  );
}

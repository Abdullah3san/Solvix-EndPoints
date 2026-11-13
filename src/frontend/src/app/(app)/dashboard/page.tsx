import type { Metadata } from "next";
import Link from "next/link";
import { Building2, GitBranch, Network, Users } from "lucide-react";
import { serverApi, serverApiOrForbidden } from "@/lib/api/server";
import type { CompanyProfileDto, TenantOverviewDto } from "@/lib/api/types";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Badge } from "@/components/ui/badge";
import { PageHeader } from "@/components/ui/page-header";
import { formatDate } from "@/lib/utils";

export const metadata: Metadata = { title: "Dashboard" };

export default async function DashboardPage() {
  const [overview, profile] = await Promise.all([
    serverApi<TenantOverviewDto>("tenant/overview"),
    serverApiOrForbidden<CompanyProfileDto>("company-profile"),
  ]);

  const stats = [
    { label: "Members", value: overview.memberCount, icon: Users, href: "/settings/users" },
    { label: "Branches", value: overview.branchCount, icon: GitBranch, href: "/organization/branches" },
    { label: "Departments", value: overview.departmentCount, icon: Network, href: "/organization/departments" },
  ];

  return (
    <>
      <PageHeader title={`Welcome to ${overview.tenant.name}`} description="A snapshot of your workspace." />
      <div className="grid gap-4 sm:grid-cols-3">
        {stats.map((stat) => (
          <Link key={stat.label} href={stat.href} className="rounded-lg">
            <Card className="transition-colors hover:border-primary/50">
              <CardContent className="flex items-center gap-4 p-5">
                <span className="flex h-10 w-10 items-center justify-center rounded-md bg-secondary text-secondary-foreground">
                  <stat.icon className="h-5 w-5" />
                </span>
                <div>
                  <p className="text-2xl font-semibold leading-none">{stat.value}</p>
                  <p className="mt-1 text-sm text-muted-foreground">{stat.label}</p>
                </div>
              </CardContent>
            </Card>
          </Link>
        ))}
      </div>

      <div className="mt-6 grid gap-4 lg:grid-cols-2">
        {profile ? (
          <Card>
            <CardHeader>
              <CardTitle className="flex items-center gap-2">
                <Building2 className="h-4 w-4 text-muted-foreground" /> Company
              </CardTitle>
              <CardDescription>Legal and contact details of your organisation.</CardDescription>
            </CardHeader>
            <CardContent className="space-y-2 text-sm">
              <Row label="Legal name" value={profile.legalName} />
              <Row label="Trade name" value={profile.tradeName} />
              <Row label="Industry" value={profile.industry} />
              <Row label="Time zone" value={profile.timeZone} />
              <Row label="Country" value={profile.address.countryCode} />
              <Link href="/organization/company" className="inline-block pt-2 text-sm font-medium text-primary hover:underline">
                View company profile →
              </Link>
            </CardContent>
          </Card>
        ) : null}
        <Card>
          <CardHeader>
            <CardTitle>Workspace</CardTitle>
            <CardDescription>Account-level information for this tenant.</CardDescription>
          </CardHeader>
          <CardContent className="space-y-2 text-sm">
            <Row label="Name" value={overview.tenant.name} />
            <Row label="Workspace id" value={overview.tenant.slug} mono />
            <div className="flex items-center justify-between gap-4 py-1">
              <span className="text-muted-foreground">Status</span>
              <Badge tone={overview.tenant.status === "Active" ? "success" : "warning"}>{overview.tenant.status}</Badge>
            </div>
            <Row label="Created" value={formatDate(overview.tenant.createdAtUtc)} />
          </CardContent>
        </Card>
      </div>
    </>
  );
}

function Row({ label, value, mono }: { label: string; value: string | null | undefined; mono?: boolean }) {
  return (
    <div className="flex items-center justify-between gap-4 py-1">
      <span className="text-muted-foreground">{label}</span>
      <span className={mono ? "font-mono text-xs" : "font-medium"}>{value || "—"}</span>
    </div>
  );
}

import type { Metadata } from "next";
import Link from "next/link";
import { serverApi } from "@/lib/api/server";
import type { ReportInfoDto } from "@/lib/api/types";
import { PageHeader } from "@/components/ui/page-header";
import { Card, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";

export const metadata: Metadata = { title: "Reports" };

export default async function ReportsPage() {
  const reports = await serverApi<ReportInfoDto[]>("reports");
  return (
    <>
      <PageHeader title="Reports" description="Inventory, compliance and helpdesk reports. Every report can be downloaded as CSV." />
      <div className="grid gap-4 md:grid-cols-2 xl:grid-cols-3">
        {reports.map((r) => (
          <Link key={r.key} href={`/reports/${r.key}`} className="block rounded-lg focus-visible:outline focus-visible:outline-2 focus-visible:outline-ring">
            <Card className="h-full transition-colors hover:bg-muted/40">
              <CardHeader>
                <CardTitle className="text-base">{r.title}</CardTitle>
                <CardDescription>{r.description}</CardDescription>
              </CardHeader>
            </Card>
          </Link>
        ))}
      </div>
    </>
  );
}

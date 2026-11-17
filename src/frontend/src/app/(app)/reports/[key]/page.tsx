import type { Metadata } from "next";
import { notFound } from "next/navigation";
import { serverApi } from "@/lib/api/server";
import { ApiError } from "@/lib/api/errors";
import type { ReportInfoDto, ReportResultDto } from "@/lib/api/types";
import { PageHeader } from "@/components/ui/page-header";
import { ReportView } from "@/components/features/reports/report-view";

export const metadata: Metadata = { title: "Report" };

export default async function ReportPage({ params }: { params: Promise<{ key: string }> }) {
  const { key } = await params;
  if (!/^[a-z-]{3,40}$/.test(key)) notFound();
  const catalog = await serverApi<ReportInfoDto[]>("reports");
  const info = catalog.find((r) => r.key === key);
  if (!info) notFound();
  let result: ReportResultDto;
  try {
    result = await serverApi<ReportResultDto>(`reports/${key}`);
  } catch (e) {
    if (e instanceof ApiError && e.status === 404) notFound();
    throw e;
  }
  return (
    <>
      <PageHeader title={info.title} description={info.description} />
      <ReportView info={info} initial={result} />
    </>
  );
}

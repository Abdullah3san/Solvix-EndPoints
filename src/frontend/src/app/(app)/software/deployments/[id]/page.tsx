import type { Metadata } from "next";
import { notFound } from "next/navigation";
import { serverApi } from "@/lib/api/server";
import { ApiError } from "@/lib/api/errors";
import type { DeploymentDetailDto, DeploymentReport } from "@/lib/api/types";
import { DeploymentDetailView } from "@/components/features/software/deployment-detail-view";

export const metadata: Metadata = { title: "Deployment" };

export default async function DeploymentPage({ params }: { params: Promise<{ id: string }> }) {
  const { id } = await params;
  if (!/^[0-9a-f-]{36}$/i.test(id)) notFound();
  const loaded = await Promise.all([
    serverApi<DeploymentDetailDto>(`software/deployments/${id}`),
    serverApi<DeploymentReport>(`software/deployments/${id}/report`),
  ]).catch((e: unknown) => {
    if (e instanceof ApiError && e.status === 404) return null;
    throw e;
  });
  if (!loaded) notFound();
  return <DeploymentDetailView initial={loaded[0]} initialReport={loaded[1]} />;
}

import type { Metadata } from "next";
import { loadDashboardOverview } from "@/lib/dashboard/overview";
import { DashboardView } from "@/components/features/dashboard/dashboard-view";

export const metadata: Metadata = { title: "Overview" };

export default async function DashboardPage() {
  return <DashboardView overview={await loadDashboardOverview()} />;
}

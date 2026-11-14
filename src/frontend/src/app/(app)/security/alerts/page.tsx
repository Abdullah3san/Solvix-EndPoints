import type { Metadata } from "next";
import { PageHeader } from "@/components/ui/page-header";
import { AlertsView } from "@/components/features/security/alerts";

export const metadata: Metadata = { title: "Alerts and events" };

export default function Page() {
  return (
    <>
      <PageHeader title="Alerts and events" description="Things that need attention now, and the Windows events behind them." />
      <AlertsView />
    </>
  );
}

import type { Metadata } from "next";
import { PageHeader } from "@/components/ui/page-header";
import { SecurityOverview } from "@/components/features/security/overview";

export const metadata: Metadata = { title: "Endpoint security" };

export default function Page() {
  return (
    <>
      <PageHeader title="Endpoint security" description="Security posture of your Windows devices, evaluated against your policy." />
      <SecurityOverview />
    </>
  );
}

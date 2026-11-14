import type { Metadata } from "next";
import { PageHeader } from "@/components/ui/page-header";
import { AdvisoriesView } from "@/components/features/security/advisories";

export const metadata: Metadata = { title: "Vulnerable software" };

export default function Page() {
  return (
    <>
      <PageHeader title="Vulnerable software" description="Advisories used to identify vulnerable installed software." />
      <AdvisoriesView />
    </>
  );
}

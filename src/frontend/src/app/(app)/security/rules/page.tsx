import type { Metadata } from "next";
import { PageHeader } from "@/components/ui/page-header";
import { RulesView } from "@/components/features/security/rules";

export const metadata: Metadata = { title: "Rules and scoring" };

export default function Page() {
  return (
    <>
      <PageHeader title="Rules and scoring" description="Every check, its severity, and how scores are calculated." />
      <RulesView />
    </>
  );
}

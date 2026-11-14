import type { Metadata } from "next";
import { PageHeader } from "@/components/ui/page-header";
import { FindingsView } from "@/components/features/security/findings";

export const metadata: Metadata = { title: "Findings" };

export default async function Page({ searchParams }: { searchParams: Promise<{ minSeverity?: string; ruleKey?: string }> }) {
  const { minSeverity, ruleKey } = await searchParams;
  return (
    <>
      <PageHeader title="Findings" description="Security issues found on devices. Select one for the evidence, severity rationale and how to fix it." />
      <FindingsView initialMinSeverity={minSeverity} initialRuleKey={ruleKey} />
    </>
  );
}

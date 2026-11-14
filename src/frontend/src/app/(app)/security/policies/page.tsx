import type { Metadata } from "next";
import { PageHeader } from "@/components/ui/page-header";
import { PoliciesView } from "@/components/features/security/policies";

export const metadata: Metadata = { title: "Policies" };

export default function Page() {
  return (
    <>
      <PageHeader title="Policies" description="Versioned security baselines. A policy takes effect only after approval." />
      <PoliciesView />
    </>
  );
}

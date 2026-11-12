import type { Metadata } from "next";
import { serverApi } from "@/lib/api/server";
import type { CompanyProfileDto } from "@/lib/api/types";
import { PageHeader } from "@/components/ui/page-header";
import { CompanyProfileForm } from "@/components/features/company-profile-form";

export const metadata: Metadata = { title: "Company profile" };

export default async function CompanyPage() {
  const profile = await serverApi<CompanyProfileDto>("company-profile");
  return (
    <>
      <PageHeader title="Company profile" description="The organisation behind this workspace." />
      <CompanyProfileForm profile={profile} />
    </>
  );
}

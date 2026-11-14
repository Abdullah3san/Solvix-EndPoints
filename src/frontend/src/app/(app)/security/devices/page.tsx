import type { Metadata } from "next";
import { PageHeader } from "@/components/ui/page-header";
import { DevicesView } from "@/components/features/security/devices";

export const metadata: Metadata = { title: "Devices" };

export default function Page() {
  return (
    <>
      <PageHeader title="Devices" description="Enrolled Windows devices and their compliance." />
      <DevicesView />
    </>
  );
}

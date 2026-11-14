import type { Metadata } from "next";
import { DeviceDetailView } from "@/components/features/security/device-detail";

export const metadata: Metadata = { title: "Device" };

export default async function Page({ params }: { params: Promise<{ id: string }> }) {
  const { id } = await params;
  return <DeviceDetailView id={id} />;
}

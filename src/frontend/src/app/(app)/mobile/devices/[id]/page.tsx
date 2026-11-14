import type { Metadata } from "next";
import { notFound } from "next/navigation";
import { ApiError } from "@/lib/api/errors";
import { serverApi } from "@/lib/api/server";
import type { DeviceDetailDto } from "@/lib/api/mdm-types";
import { DeviceDetail } from "@/components/features/mobile/device-detail";

export const metadata: Metadata = { title: "Device" };

export default async function MobileDevicePage({ params }: { params: Promise<{ id: string }> }) {
  const { id } = await params;
  let detail: DeviceDetailDto;
  try {
    detail = await serverApi<DeviceDetailDto>(`mobile-devices/${encodeURIComponent(id)}`);
  } catch (error) {
    // Unknown ids and ids from other workspaces are both 404 (or a malformed id, 400).
    if (error instanceof ApiError && (error.status === 404 || error.status === 400)) notFound();
    throw error;
  }
  return <DeviceDetail initial={detail} />;
}

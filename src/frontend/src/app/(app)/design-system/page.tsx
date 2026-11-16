import type { Metadata } from "next";
import { notFound } from "next/navigation";
import { isDevelopment } from "@/lib/env";
import { DesignSystemShowcase } from "@/components/features/design-system/showcase";

export const metadata: Metadata = { title: "Design system" };

/**
 * Living reference for the design system. It renders sample content, so it is
 * only served by the development server; production builds return 404.
 */
export default function DesignSystemPage() {
  if (!isDevelopment) notFound();
  return <DesignSystemShowcase />;
}

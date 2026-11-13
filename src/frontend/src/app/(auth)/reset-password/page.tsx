import type { Metadata } from "next";
import { ResetPasswordForm } from "@/components/features/account-link-forms";
import { fetchPasswordRules } from "@/lib/api/public";

export const metadata: Metadata = { title: "Choose a new password", referrer: "no-referrer" };
export const dynamic = "force-dynamic";

export default async function ResetPasswordPage() {
  return <ResetPasswordForm rules={await fetchPasswordRules()} />;
}

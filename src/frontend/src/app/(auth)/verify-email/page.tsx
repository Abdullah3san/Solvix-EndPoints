import type { Metadata } from "next";
import { VerifyEmailForm } from "@/components/features/account-link-forms";

export const metadata: Metadata = { title: "Confirm e-mail address", referrer: "no-referrer" };

export default function VerifyEmailPage() {
  return <VerifyEmailForm />;
}

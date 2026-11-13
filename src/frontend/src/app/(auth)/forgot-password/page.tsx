import type { Metadata } from "next";
import { ForgotPasswordForm } from "@/components/features/account-link-forms";

export const metadata: Metadata = { title: "Reset password" };

export default function ForgotPasswordPage() {
  return <ForgotPasswordForm />;
}

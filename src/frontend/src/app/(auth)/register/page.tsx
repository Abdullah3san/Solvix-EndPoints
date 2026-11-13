import type { Metadata } from "next";
import { RegisterForm } from "@/components/features/auth-forms";
import { fetchPasswordRules } from "@/lib/api/public";

export const metadata: Metadata = { title: "Create workspace" };
export const dynamic = "force-dynamic";

export default async function RegisterPage() {
  return <RegisterForm rules={await fetchPasswordRules()} />;
}

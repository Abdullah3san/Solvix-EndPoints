import { Suspense } from "react";
import type { Metadata } from "next";
import { RegisterForm } from "@/components/features/auth-forms";

export const metadata: Metadata = { title: "Create workspace" };

export default function RegisterPage() {
  return (
    <Suspense>
      <RegisterForm />
    </Suspense>
  );
}

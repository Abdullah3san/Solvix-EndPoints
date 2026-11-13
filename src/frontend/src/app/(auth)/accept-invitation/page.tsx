import type { Metadata } from "next";
import { AcceptInvitationForm } from "@/components/features/account-link-forms";

export const metadata: Metadata = { title: "Accept invitation", referrer: "no-referrer" };

export default function AcceptInvitationPage() {
  return <AcceptInvitationForm />;
}

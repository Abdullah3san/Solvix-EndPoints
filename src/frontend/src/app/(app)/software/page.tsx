import { redirect } from "next/navigation";

/** The "Software" breadcrumb points here. */
export default function SoftwarePage() {
  redirect("/software/deployments");
}

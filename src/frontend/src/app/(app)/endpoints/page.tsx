import { redirect } from "next/navigation";

/** The "Endpoints" breadcrumb points here. */
export default function EndpointsPage() {
  redirect("/endpoints/devices");
}

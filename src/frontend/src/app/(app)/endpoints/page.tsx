import { redirect } from "next/navigation";

/** The "Endpoints" breadcrumb segment has no page of its own. */
export default function EndpointsIndex() {
  redirect("/endpoints/devices");
}

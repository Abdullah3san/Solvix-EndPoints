import { redirect } from "next/navigation";

/** /settings has no page of its own (breadcrumb target): go to the workspace settings. */
export default function SettingsIndex() {
  redirect("/settings/workspace");
}

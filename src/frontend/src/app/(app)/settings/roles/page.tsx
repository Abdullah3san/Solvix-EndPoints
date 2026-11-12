import type { Metadata } from "next";
import { serverApiOrForbidden } from "@/lib/api/server";
import type { PermissionDto, RoleDto } from "@/lib/api/types";
import { PageHeader } from "@/components/ui/page-header";
import { NoAccess } from "@/components/features/no-access";
import { RolesView } from "@/components/features/roles-view";

export const metadata: Metadata = { title: "Roles" };

export default async function RolesPage() {
  const [roles, catalog] = await Promise.all([
    serverApiOrForbidden<RoleDto[]>("roles"),
    serverApiOrForbidden<PermissionDto[]>("roles/permissions"),
  ]);
  return (
    <>
      <PageHeader title="Roles" description="A role is a named set of permissions. Access is always decided by permissions, never by role names." />
      {roles && catalog ? <RolesView initialRoles={roles} catalog={catalog} /> : <NoAccess what="roles" />}
    </>
  );
}

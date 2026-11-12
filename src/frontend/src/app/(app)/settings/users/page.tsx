import type { Metadata } from "next";
import { serverApiOrForbidden } from "@/lib/api/server";
import type { InvitationDto, PagedResult, RoleDto, TenantUserDto } from "@/lib/api/types";
import { PageHeader } from "@/components/ui/page-header";
import { NoAccess } from "@/components/features/no-access";
import { UsersView } from "@/components/features/users-view";

export const metadata: Metadata = { title: "Users" };

export default async function UsersPage() {
  const [users, invitations, roles] = await Promise.all([
    serverApiOrForbidden<PagedResult<TenantUserDto>>("users?pageSize=200"),
    serverApiOrForbidden<PagedResult<InvitationDto>>("invitations?pageSize=100"),
    serverApiOrForbidden<RoleDto[]>("roles"),
  ]);
  return (
    <>
      <PageHeader title="Users" description="People in this workspace, their roles and pending invitations." />
      {users && invitations ? <UsersView initialUsers={users} initialInvitations={invitations} roles={roles} /> : <NoAccess what="the user list" />}
    </>
  );
}

import type { Metadata } from "next";
import { serverApi } from "@/lib/api/server";
import type { DeploymentGroupDto, DeploymentPolicyDto, DeviceGroupDto, BranchDto, PagedResult, RestrictionRuleDto } from "@/lib/api/types";
import { PageHeader } from "@/components/ui/page-header";
import { PoliciesView } from "@/components/features/software/policies-view";

export const metadata: Metadata = { title: "Software policies" };

export default async function PoliciesPage() {
  const [policy, rules, groups, deviceGroups, branches] = await Promise.all([
    serverApi<DeploymentPolicyDto>("software/policy"),
    serverApi<RestrictionRuleDto[]>("software/restriction-rules"),
    serverApi<DeploymentGroupDto[]>("software/deployment-groups"),
    serverApi<PagedResult<DeviceGroupDto>>("device-groups?pageSize=200"),
    serverApi<PagedResult<BranchDto>>("branches?pageSize=200"),
  ]);
  return (
    <>
      <PageHeader title="Software policies" description="Deployment policy, deployment groups and software restriction rules." />
      <PoliciesView policy={policy} rules={rules} groups={groups} deviceGroups={deviceGroups.items} branches={branches.items} />
    </>
  );
}

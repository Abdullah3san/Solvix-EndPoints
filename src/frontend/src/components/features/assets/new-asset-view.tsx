"use client";

import * as React from "react";
import Link from "next/link";
import { useRouter } from "next/navigation";
import { api } from "@/lib/api/client";
import type { AssetDetailsDto, BranchDto, DepartmentDto, TenantUserDto, VendorDto } from "@/lib/api/types";
import { emptyToNull, fieldErrorsFrom, messageFrom } from "@/lib/forms";
import { Button, buttonClasses } from "@/components/ui/button";
import { useToast } from "@/components/providers/toast-provider";
import { AssetForm, emptyAssetForm, toAssetInput, type AssetFormValues } from "./asset-form";

export function NewAssetView({ branches, departments, vendors, members, defaultCurrency }: { branches: BranchDto[]; departments: DepartmentDto[]; vendors: VendorDto[]; members: TenantUserDto[]; defaultCurrency: string }) {
  const router = useRouter();
  const { toast } = useToast();
  const [values, setValues] = React.useState<AssetFormValues>(() => emptyAssetForm(defaultCurrency));
  const [errors, setErrors] = React.useState<Record<string, string>>({});
  const [busy, setBusy] = React.useState(false);

  async function submit(e: React.FormEvent, another = false) {
    e.preventDefault();
    const { input, errors: local } = toAssetInput(values);
    if (values.assignTo === "member" && !values.assignedUserId) local.assignedUserId = "Choose a member.";
    if (values.assignTo === "employee" && !values.assigneeName.trim()) local.assigneeName = "Enter the employee's name.";
    if (Object.keys(local).length) return setErrors(local);
    setBusy(true);
    setErrors({});
    try {
      const assignTo = values.assignTo
        ? {
            assignedUserId: values.assignTo === "member" ? values.assignedUserId : null,
            assigneeName: values.assignTo === "employee" ? values.assigneeName.trim() : null,
            employeeNumber: emptyToNull(values.employeeNumber),
            expectedReturnDate: null,
            notes: null,
          }
        : null;
      const created = await api.post<AssetDetailsDto>("assets", { asset: input, assignTo });
      toast({ tone: "success", title: `Registered ${created.summary.assetTag}` });
      if (another) {
        // Keep purchase details for batch registration; clear the unit-specific fields.
        setValues((v) => ({ ...v, assetTag: "", serialNumber: "", name: v.name, assignTo: "", assignedUserId: "", assigneeName: "", employeeNumber: "" }));
      } else {
        router.push(`/assets/${created.summary.id}`);
      }
    } catch (err) {
      setErrors(fieldErrorsFrom(err));
      toast({ tone: "error", title: "Could not register asset", description: messageFrom(err) });
    } finally {
      setBusy(false);
    }
  }

  return (
    <form onSubmit={(e) => submit(e)} className="max-w-4xl space-y-4">
      <AssetForm values={values} onChange={(p) => setValues((v) => ({ ...v, ...p }))} errors={errors} mode="create" branches={branches} departments={departments} vendors={vendors} members={members} />
      <div className="flex flex-wrap justify-end gap-2">
        <Link href="/assets" className={buttonClasses({ variant: "outline" })}>
          Cancel
        </Link>
        <Button variant="outline" loading={busy} onClick={(e) => submit(e, true)}>
          Save and add another
        </Button>
        <Button type="submit" loading={busy}>
          Register asset
        </Button>
      </div>
    </form>
  );
}

"use client";

import * as React from "react";
import type { AssetCategory, AssetCondition, AssetDetailsDto, AssetInput, BranchDto, DepartmentDto, DepreciationMethod, TenantUserDto, VendorDto } from "@/lib/api/types";
import { CATEGORIES, CONDITIONS } from "@/lib/assets";
import { emptyToNull } from "@/lib/forms";
import { Field } from "@/components/ui/field";
import { Input, Select, Textarea } from "@/components/ui/input";

export interface AssetFormValues {
  assetTag: string;
  name: string;
  category: AssetCategory;
  condition: AssetCondition;
  manufacturer: string;
  model: string;
  serialNumber: string;
  description: string;
  location: string;
  branchId: string;
  departmentId: string;
  vendorId: string;
  purchaseDate: string;
  purchaseCost: string;
  currency: string;
  purchaseOrderNumber: string;
  invoiceNumber: string;
  warrantyStartDate: string;
  warrantyEndDate: string;
  warrantyProvider: string;
  warrantyReference: string;
  depreciationMethod: DepreciationMethod;
  usefulLifeMonths: string;
  salvageValue: string;
  assignTo: "" | "member" | "employee";
  assignedUserId: string;
  assigneeName: string;
  employeeNumber: string;
}

export function emptyAssetForm(currency = ""): AssetFormValues {
  return {
    assetTag: "",
    name: "",
    category: "Laptop",
    condition: "New",
    manufacturer: "",
    model: "",
    serialNumber: "",
    description: "",
    location: "",
    branchId: "",
    departmentId: "",
    vendorId: "",
    purchaseDate: "",
    purchaseCost: "",
    currency,
    purchaseOrderNumber: "",
    invoiceNumber: "",
    warrantyStartDate: "",
    warrantyEndDate: "",
    warrantyProvider: "",
    warrantyReference: "",
    depreciationMethod: "None",
    usefulLifeMonths: "",
    salvageValue: "",
    assignTo: "",
    assignedUserId: "",
    assigneeName: "",
    employeeNumber: "",
  };
}

export function formFromAsset(asset: AssetDetailsDto): AssetFormValues {
  const s = asset.summary;
  const text = (v: string | number | null | undefined) => (v === null || v === undefined ? "" : String(v));
  return {
    ...emptyAssetForm(),
    assetTag: s.assetTag,
    name: s.name,
    category: s.category,
    condition: s.condition,
    manufacturer: text(s.manufacturer),
    model: text(s.model),
    serialNumber: text(s.serialNumber),
    description: text(asset.description),
    location: text(s.location),
    branchId: s.branch?.id ?? "",
    departmentId: s.department?.id ?? "",
    vendorId: s.vendor?.id ?? "",
    purchaseDate: text(s.purchaseDate),
    purchaseCost: text(s.purchaseCost),
    currency: text(s.currency),
    purchaseOrderNumber: text(asset.purchaseOrderNumber),
    invoiceNumber: text(asset.invoiceNumber),
    warrantyStartDate: text(asset.warrantyStartDate),
    warrantyEndDate: text(s.warrantyEndDate),
    warrantyProvider: text(asset.warrantyProvider),
    warrantyReference: text(asset.warrantyReference),
    depreciationMethod: asset.depreciationMethod,
    usefulLifeMonths: text(asset.usefulLifeMonths),
    salvageValue: text(asset.salvageValue),
  };
}

function numberOrNull(value: string): number | null {
  const trimmed = value.trim();
  if (!trimmed) return null;
  const n = Number(trimmed);
  return Number.isFinite(n) ? n : NaN;
}

/** Converts form values to the API input; returns field errors for values the browser cannot parse. */
export function toAssetInput(values: AssetFormValues): { input: AssetInput; errors: Record<string, string> } {
  const errors: Record<string, string> = {};
  const cost = numberOrNull(values.purchaseCost);
  const salvage = numberOrNull(values.salvageValue);
  const life = numberOrNull(values.usefulLifeMonths);
  if (Number.isNaN(cost)) errors.purchaseCost = "Enter a number.";
  if (Number.isNaN(salvage)) errors.salvageValue = "Enter a number.";
  if (Number.isNaN(life) || (life !== null && !Number.isInteger(life))) errors.usefulLifeMonths = "Enter a whole number of months.";
  if (!values.name.trim()) errors.name = "Name is required.";
  return {
    errors,
    input: {
      assetTag: emptyToNull(values.assetTag),
      name: values.name.trim(),
      category: values.category,
      condition: values.condition,
      manufacturer: emptyToNull(values.manufacturer),
      model: emptyToNull(values.model),
      serialNumber: emptyToNull(values.serialNumber),
      description: emptyToNull(values.description),
      location: emptyToNull(values.location),
      branchId: emptyToNull(values.branchId),
      departmentId: emptyToNull(values.departmentId),
      vendorId: emptyToNull(values.vendorId),
      purchaseDate: emptyToNull(values.purchaseDate),
      purchaseCost: Number.isNaN(cost) ? null : cost,
      currency: emptyToNull(values.currency)?.toUpperCase() ?? null,
      purchaseOrderNumber: emptyToNull(values.purchaseOrderNumber),
      invoiceNumber: emptyToNull(values.invoiceNumber),
      warrantyStartDate: emptyToNull(values.warrantyStartDate),
      warrantyEndDate: emptyToNull(values.warrantyEndDate),
      warrantyProvider: emptyToNull(values.warrantyProvider),
      warrantyReference: emptyToNull(values.warrantyReference),
      depreciationMethod: values.depreciationMethod,
      usefulLifeMonths: Number.isNaN(life) ? null : life,
      salvageValue: Number.isNaN(salvage) ? null : salvage,
    },
  };
}

interface AssetFormProps {
  values: AssetFormValues;
  onChange: (patch: Partial<AssetFormValues>) => void;
  errors: Record<string, string>;
  mode: "create" | "edit";
  branches: BranchDto[];
  departments: DepartmentDto[];
  vendors: VendorDto[];
  members?: TenantUserDto[];
}

function Section({ title, description, children }: { title: string; description?: string; children: React.ReactNode }) {
  return (
    <fieldset className="space-y-3 rounded-lg border border-border p-4">
      <legend className="px-1 text-sm font-semibold">{title}</legend>
      {description ? <p className="-mt-1 text-xs text-muted-foreground">{description}</p> : null}
      <div className="grid gap-3 sm:grid-cols-2">{children}</div>
    </fieldset>
  );
}

export function AssetForm({ values, onChange, errors, mode, branches, departments, vendors, members = [] }: AssetFormProps) {
  const id = React.useId();
  const f = (name: string) => `${id}-${name}`;
  const branchDepartments = departments.filter((d) => !values.branchId || d.branchId === values.branchId);
  const category = CATEGORIES.find((c) => c.value === values.category);

  const text = (name: keyof AssetFormValues, label: string, props: React.InputHTMLAttributes<HTMLInputElement> = {}, hint?: string) => (
    <Field label={label} htmlFor={f(name)} error={errors[name]} hint={hint} required={props.required}>
      <Input id={f(name)} value={values[name] as string} onChange={(e) => onChange({ [name]: e.target.value } as Partial<AssetFormValues>)} aria-invalid={!!errors[name]} {...props} />
    </Field>
  );

  return (
    <div className="space-y-4">
      <Section title="Identification" description={category?.manageable ? "Desktops, laptops, servers and tablets are matched to managed endpoints by serial number and manufacturer." : undefined}>
        {mode === "create"
          ? text("assetTag", "Asset tag", { placeholder: "Generated automatically", maxLength: 40 }, "Leave empty to number automatically, or enter an existing label.")
          : text("assetTag", "Asset tag", { disabled: true })}
        {text("name", "Name", { required: true, maxLength: 200 })}
        <Field label="Category" htmlFor={f("category")} required error={errors.category}>
          <Select id={f("category")} value={values.category} onChange={(e) => onChange({ category: e.target.value as AssetCategory })}>
            {CATEGORIES.map((c) => (
              <option key={c.value} value={c.value}>
                {c.label}
              </option>
            ))}
          </Select>
        </Field>
        <Field label="Condition" htmlFor={f("condition")}>
          <Select id={f("condition")} value={values.condition} onChange={(e) => onChange({ condition: e.target.value as AssetCondition })}>
            {CONDITIONS.map((c) => (
              <option key={c}>{c}</option>
            ))}
          </Select>
        </Field>
        {text("manufacturer", "Manufacturer", { maxLength: 128, placeholder: "e.g. Dell Inc." })}
        {text("model", "Model", { maxLength: 128 })}
        {text("serialNumber", "Serial number", { maxLength: 128, autoComplete: "off" }, "Must be unique per manufacturer.")}
      </Section>

      {mode === "create" ? (
        <Section title="Location" description="Later moves are recorded as transfers.">
          <Field label="Branch" htmlFor={f("branchId")} error={errors.branchId}>
            <Select id={f("branchId")} value={values.branchId} onChange={(e) => onChange({ branchId: e.target.value, departmentId: "" })}>
              <option value="">No branch</option>
              {branches.map((b) => (
                <option key={b.id} value={b.id}>
                  {b.name}
                </option>
              ))}
            </Select>
          </Field>
          <Field label="Department" htmlFor={f("departmentId")} error={errors.departmentId}>
            <Select id={f("departmentId")} value={values.departmentId} onChange={(e) => onChange({ departmentId: e.target.value })}>
              <option value="">No department</option>
              {branchDepartments.map((d) => (
                <option key={d.id} value={d.id}>
                  {d.name}
                </option>
              ))}
            </Select>
          </Field>
          {text("location", "Location", { maxLength: 200, placeholder: "Room, rack or shelf" })}
        </Section>
      ) : null}

      <Section title="Purchase">
        <Field label="Vendor" htmlFor={f("vendorId")} error={errors.vendorId}>
          <Select id={f("vendorId")} value={values.vendorId} onChange={(e) => onChange({ vendorId: e.target.value })}>
            <option value="">No vendor</option>
            {vendors.map((v) => (
              <option key={v.id} value={v.id}>
                {v.name}
              </option>
            ))}
          </Select>
        </Field>
        {text("purchaseDate", "Purchase date", { type: "date" })}
        {text("purchaseCost", "Purchase cost", { inputMode: "decimal" })}
        {text("currency", "Currency", { maxLength: 3, placeholder: "Workspace default" })}
        {text("purchaseOrderNumber", "PO number", { maxLength: 100 })}
        {text("invoiceNumber", "Invoice number", { maxLength: 100 })}
      </Section>

      <Section title="Warranty">
        {text("warrantyStartDate", "Starts", { type: "date" })}
        {text("warrantyEndDate", "Ends", { type: "date" })}
        {text("warrantyProvider", "Provider", { maxLength: 128 })}
        {text("warrantyReference", "Contract / reference", { maxLength: 100 })}
      </Section>

      <Section title="Depreciation" description="Needs a purchase date and cost. Charged monthly from the purchase date.">
        <Field label="Method" htmlFor={f("depreciationMethod")} error={errors.depreciationMethod}>
          <Select
            id={f("depreciationMethod")}
            value={values.depreciationMethod}
            onChange={(e) => {
              const method = e.target.value as DepreciationMethod;
              onChange({ depreciationMethod: method, usefulLifeMonths: values.usefulLifeMonths || (method === "None" ? "" : String(defaultLife(values.category))) });
            }}
          >
            <option value="None">Not depreciated</option>
            <option value="StraightLine">Straight-line</option>
            <option value="DecliningBalance">Double-declining balance</option>
          </Select>
        </Field>
        {text("usefulLifeMonths", "Useful life (months)", { inputMode: "numeric", disabled: values.depreciationMethod === "None" })}
        {text("salvageValue", "Salvage value", { inputMode: "decimal", disabled: values.depreciationMethod === "None" })}
      </Section>

      {mode === "create" ? (
        <Section title="Initial assignment" description="Optional: check the asset out immediately.">
          <Field label="Assign to" htmlFor={f("assignTo")}>
            <Select id={f("assignTo")} value={values.assignTo} onChange={(e) => onChange({ assignTo: e.target.value as AssetFormValues["assignTo"] })}>
              <option value="">Keep in stock</option>
              <option value="member">Workspace member</option>
              <option value="employee">Employee without a login</option>
            </Select>
          </Field>
          {values.assignTo === "member" ? (
            <Field label="Member" htmlFor={f("assignedUserId")} error={errors.assignedUserId}>
              <Select id={f("assignedUserId")} value={values.assignedUserId} onChange={(e) => onChange({ assignedUserId: e.target.value })}>
                <option value="">Choose…</option>
                {members.filter((m) => m.isActive).map((m) => (
                  <option key={m.userId} value={m.userId}>
                    {m.displayName} ({m.email})
                  </option>
                ))}
              </Select>
            </Field>
          ) : values.assignTo === "employee" ? (
            <>
              {text("assigneeName", "Employee name", { maxLength: 200 })}
              {text("employeeNumber", "Employee number", { maxLength: 50 })}
            </>
          ) : null}
        </Section>
      ) : null}

      <Field label="Description / notes" htmlFor={f("description")} error={errors.description}>
        <Textarea id={f("description")} value={values.description} maxLength={4000} onChange={(e) => onChange({ description: e.target.value })} />
      </Field>
    </div>
  );
}

function defaultLife(category: AssetCategory): number {
  const defaults: Partial<Record<AssetCategory, number>> = { Laptop: 36, Tablet: 36, MobilePhone: 24, Server: 60, NetworkSwitch: 84, Router: 84 };
  return defaults[category] ?? 48;
}

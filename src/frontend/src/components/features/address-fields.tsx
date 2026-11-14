"use client";

import { Field } from "@/components/ui/field";
import { Input } from "@/components/ui/input";
import type { AddressDto } from "@/lib/api/types";

interface AddressFieldsProps {
  value: AddressDto;
  onChange: (next: AddressDto) => void;
  errors?: Record<string, string>;
  idPrefix?: string;
}

export function AddressFields({ value, onChange, errors = {}, idPrefix = "address" }: AddressFieldsProps) {
  const set = (key: keyof AddressDto) => (event: React.ChangeEvent<HTMLInputElement>) =>
    onChange({ ...value, [key]: event.target.value || null });

  return (
    <fieldset className="grid gap-4 sm:grid-cols-2">
      <legend className="mb-1 text-sm font-medium">Address</legend>
      <Field label="Address line 1" htmlFor={`${idPrefix}-line1`} error={errors["address.line1"]} className="sm:col-span-2">
        <Input id={`${idPrefix}-line1`} value={value.line1 ?? ""} onChange={set("line1")} autoComplete="address-line1" />
      </Field>
      <Field label="Address line 2" htmlFor={`${idPrefix}-line2`} error={errors["address.line2"]} className="sm:col-span-2">
        <Input id={`${idPrefix}-line2`} value={value.line2 ?? ""} onChange={set("line2")} autoComplete="address-line2" />
      </Field>
      <Field label="City" htmlFor={`${idPrefix}-city`} error={errors["address.city"]}>
        <Input id={`${idPrefix}-city`} value={value.city ?? ""} onChange={set("city")} autoComplete="address-level2" />
      </Field>
      <Field label="Region / State" htmlFor={`${idPrefix}-region`} error={errors["address.region"]}>
        <Input id={`${idPrefix}-region`} value={value.region ?? ""} onChange={set("region")} autoComplete="address-level1" />
      </Field>
      <Field label="Postal code" htmlFor={`${idPrefix}-postal`} error={errors["address.postalCode"]}>
        <Input id={`${idPrefix}-postal`} value={value.postalCode ?? ""} onChange={set("postalCode")} autoComplete="postal-code" />
      </Field>
      <Field label="Country (ISO 2)" htmlFor={`${idPrefix}-country`} error={errors["address.countryCode"]} hint="e.g. DE, US">
        <Input id={`${idPrefix}-country`} value={value.countryCode ?? ""} onChange={set("countryCode")} maxLength={2} autoComplete="country" className="uppercase" />
      </Field>
    </fieldset>
  );
}

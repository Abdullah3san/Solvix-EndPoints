"use client";

import { Checkbox } from "@/components/ui/input";
import { Badge } from "@/components/ui/badge";
import type { Permission, RoleDto } from "@/lib/api/types";
import { missingPermissions } from "@/lib/permissions";

/**
 * Checkbox list of roles. Roles that would grant something the current user
 * does not hold are disabled and say why: the API refuses such assignments
 * (privilege ceiling), so offering them would only produce an error.
 */
export function RolePicker({
  roles,
  selected,
  onChange,
  actorPermissions,
  idPrefix,
}: {
  roles: RoleDto[];
  selected: string[];
  onChange: (next: string[]) => void;
  actorPermissions: readonly Permission[];
  idPrefix: string;
}) {
  return (
    <ul className="max-h-80 space-y-1 overflow-y-auto rounded-md border border-border p-2" aria-label="Roles">
      {roles.map((role) => {
        const missing = missingPermissions(role.permissions, actorPermissions);
        const checked = selected.includes(role.id);
        const disabled = missing.length > 0 && !checked;
        const id = `${idPrefix}-${role.id}`;
        return (
          <li key={role.id} className="flex items-start gap-3 rounded px-2 py-1.5 hover:bg-muted/50">
            <Checkbox
              id={id}
              checked={checked}
              disabled={disabled}
              onChange={(e) => onChange(e.target.checked ? [...selected, role.id] : selected.filter((r) => r !== role.id))}
              className="mt-0.5"
            />
            <label htmlFor={id} className={disabled ? "cursor-not-allowed opacity-60" : "cursor-pointer"}>
              <span className="flex items-center gap-2 text-sm font-medium">
                {role.name}
                {role.isSystem ? <Badge>Built-in</Badge> : <Badge tone="brand">Custom</Badge>}
              </span>
              {role.description ? <span className="block text-xs text-muted-foreground">{role.description}</span> : null}
              {disabled ? (
                <span className="block text-xs text-destructive">You cannot grant this role: it includes {missing.join(", ")}.</span>
              ) : null}
            </label>
          </li>
        );
      })}
    </ul>
  );
}

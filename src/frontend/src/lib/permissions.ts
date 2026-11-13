import type { Permission, PermissionDto } from "@/lib/api/types";

/** Groups the catalog for display, keeping the API's order. */
export function groupPermissions(catalog: PermissionDto[]): { group: string; items: PermissionDto[] }[] {
  const groups = new Map<string, PermissionDto[]>();
  for (const p of catalog) {
    const list = groups.get(p.group) ?? [];
    list.push(p);
    groups.set(p.group, list);
  }
  return [...groups].map(([group, items]) => ({ group, items }));
}

/** Adds everything the selected permissions imply (e.g. users.manage → users.view), as the API does. */
export function expandPermissions(selected: Iterable<Permission>, catalog: PermissionDto[]): Set<Permission> {
  const byKey = new Map(catalog.map((p) => [p.key, p]));
  const result = new Set<Permission>();
  const stack = [...selected];
  while (stack.length > 0) {
    const key = stack.pop()!;
    if (result.has(key) || !byKey.has(key)) continue;
    result.add(key);
    stack.push(...byKey.get(key)!.implies);
  }
  return result;
}

/** Permissions the given role set grants that the actor does not hold: what the API will refuse to let them assign. */
export function missingPermissions(granted: Iterable<Permission>, actor: readonly Permission[]): Permission[] {
  const held = new Set(actor);
  return [...new Set(granted)].filter((p) => !held.has(p)).sort();
}

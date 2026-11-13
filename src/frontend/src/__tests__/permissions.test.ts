import { describe, expect, it } from "vitest";
import type { PermissionDto } from "@/lib/api/types";
import { expandPermissions, groupPermissions, missingPermissions } from "@/lib/permissions";
import { hasPermission } from "@/components/providers/session-provider";
import { safeNext } from "@/components/features/auth-forms";
import { describeAgent } from "@/components/features/account-security";

const catalog: PermissionDto[] = [
  { key: "users.view", group: "Users", description: "", implies: [] },
  { key: "users.manage", group: "Users", description: "", implies: ["users.view"] },
  { key: "devices.view", group: "Devices", description: "", implies: [] },
  { key: "mdm.manage", group: "MDM", description: "", implies: ["devices.view"] },
];

describe("permission helpers", () => {
  it("expands implied permissions like the API", () => {
    expect([...expandPermissions(["users.manage", "mdm.manage"], catalog)].sort()).toEqual(["devices.view", "mdm.manage", "users.manage", "users.view"]);
  });

  it("reports what a role would grant beyond the actor's own permissions", () => {
    expect(missingPermissions(["users.view", "users.manage", "devices.view"], ["users.view", "users.manage"])).toEqual(["devices.view"]);
    expect(missingPermissions(["users.view"], ["users.view", "users.manage"])).toEqual([]);
  });

  it("groups the catalog in API order", () => {
    expect(groupPermissions(catalog).map((g) => g.group)).toEqual(["Users", "Devices", "MDM"]);
  });

  it("treats every permission as unusable while MFA enrolment is required", () => {
    expect(hasPermission({ permissions: ["users.view"], mfaEnrollmentRequired: false }, "users.view")).toBe(true);
    expect(hasPermission({ permissions: ["users.view"], mfaEnrollmentRequired: true }, "users.view")).toBe(false);
    expect(hasPermission({ permissions: [], mfaEnrollmentRequired: true }, undefined)).toBe(true);
  });
});

describe("post-login redirect", () => {
  it.each([
    ["/settings/users", "/settings/users"],
    ["/organization/branches?page=2", "/organization/branches?page=2"],
    ["//evil.test", "/dashboard"],
    ["/\\evil.test", "/dashboard"],
    ["https://evil.test", "/dashboard"],
    ["javascript:alert(1)", "/dashboard"],
    [null, "/dashboard"],
  ])("%s -> %s", (input, expected) => {
    expect(safeNext(input)).toBe(expected);
  });
});

describe("session list", () => {
  it("names devices from the user agent", () => {
    expect(describeAgent("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0 Safari/537.36")).toBe("Chrome on Windows");
    expect(describeAgent("Mozilla/5.0 (Macintosh; Intel Mac OS X 14_5) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Safari/605.1.15")).toBe("Safari on macOS");
    expect(describeAgent(null)).toBe("Unknown device");
  });
});

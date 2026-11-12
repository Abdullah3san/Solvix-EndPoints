// DTOs mirroring the Solvix API (System.Text.Json web defaults: camelCase, enums as strings).

export type TenantStatus = "Active" | "Suspended" | "PendingDeletion";

/** Permission keys. Mirrors the API's permission catalog; authorization is always decided by the API. */
export type Permission =
  | "devices.view"
  | "devices.enroll"
  | "devices.manage"
  | "devices.remote_access"
  | "devices.execute_command"
  | "assets.view"
  | "assets.create"
  | "assets.edit"
  | "assets.transfer"
  | "mdm.manage"
  | "users.view"
  | "users.manage"
  | "roles.view"
  | "roles.manage"
  | "security.view"
  | "security.manage"
  | "audit.view"
  | "reports.view"
  | "billing.manage"
  | "organization.view"
  | "organization.manage"
  | "tenant.manage";

export interface UserDto {
  id: string;
  email: string;
  displayName: string;
}

export interface AuthTenantDto {
  id: string;
  slug: string;
  name: string;
}

/** Full token response of the API. Only ever handled by the BFF route handlers, never sent to the browser. */
export interface AuthResponse {
  accessToken: string;
  accessTokenExpiresAtUtc: string;
  refreshToken: string;
  refreshTokenExpiresAtUtc: string;
  sessionId: string;
  user: UserDto;
  tenant: AuthTenantDto;
}

export interface MfaChallengeDto {
  mfaToken: string;
  expiresAtUtc: string;
}

export interface LoginResponse {
  mfaRequired: boolean;
  session: AuthResponse | null;
  challenge: MfaChallengeDto | null;
}

/** What the BFF tells the browser about a new session. */
export interface PublicSession {
  user: UserDto;
  tenant: AuthTenantDto;
  sessionId: string;
}

export interface CurrentUserDto {
  user: UserDto;
  tenant: AuthTenantDto;
  sessionId: string;
  roles: string[];
  permissions: Permission[];
  mfaEnabled: boolean;
  mfaVerified: boolean;
  mfaEnrollmentRequired: boolean;
}

export interface RegisterTenantResponse {
  tenantId: string;
  slug: string;
  name: string;
  emailVerificationRequired: boolean;
  session: AuthResponse | null;
}

export interface PasswordRulesDto {
  minLength: number;
  maxLength: number;
  requireUppercase: boolean;
  requireLowercase: boolean;
  requireDigit: boolean;
  requireSymbol: boolean;
  historyCount: number;
}

export interface RoleSummaryDto {
  id: string;
  name: string;
  isSystem: boolean;
}

export interface TenantUserDto {
  userId: string;
  membershipId: string;
  email: string;
  displayName: string;
  isActive: boolean;
  emailVerified: boolean;
  mfaEnabled: boolean;
  roles: RoleSummaryDto[];
  joinedAtUtc: string;
  deactivatedAtUtc: string | null;
}

export type InvitationStatus = "Pending" | "Accepted" | "Revoked" | "Expired";

export interface InvitationDto {
  id: string;
  email: string;
  displayName: string | null;
  status: InvitationStatus;
  roles: RoleSummaryDto[];
  invitedByUserId: string;
  createdAtUtc: string;
  expiresAtUtc: string;
  acceptedAtUtc: string | null;
}

export interface InvitationPreviewDto {
  tenantName: string;
  email: string;
  displayName: string | null;
  accountExists: boolean;
  roles: string[];
  expiresAtUtc: string;
  passwordRules: PasswordRulesDto;
}

export interface InvitationAcceptedDto {
  tenantSlug: string;
  tenantName: string;
  email: string;
  accountCreated: boolean;
}

export interface RoleDto {
  id: string;
  name: string;
  description: string | null;
  isSystem: boolean;
  systemKey: string | null;
  permissions: Permission[];
  memberCount: number;
  createdAtUtc: string;
  updatedAtUtc: string;
}

export interface PermissionDto {
  key: Permission;
  group: string;
  description: string;
  implies: Permission[];
}

export interface SessionDto {
  id: string;
  tenantId: string;
  tenantName: string;
  createdAtUtc: string;
  lastSeenAtUtc: string;
  expiresAtUtc: string;
  ipAddress: string | null;
  userAgent: string | null;
  mfaVerified: boolean;
  isCurrent: boolean;
}

export interface SecurityPolicySettings {
  passwordMinLength: number;
  passwordRequireUppercase: boolean;
  passwordRequireLowercase: boolean;
  passwordRequireDigit: boolean;
  passwordRequireSymbol: boolean;
  passwordHistoryCount: number;
  passwordMaxAgeDays: number;
  sessionIdleTimeoutMinutes: number;
  sessionAbsoluteLifetimeHours: number;
  requireMfa: boolean;
}

export interface SecurityPolicyDto {
  policy: SecurityPolicySettings;
  baseline: SecurityPolicySettings;
  updatedAtUtc: string;
}

export type AuditOutcome = "Success" | "Failure";

export interface AuditEventDto {
  id: string;
  occurredAtUtc: string;
  eventType: string;
  outcome: AuditOutcome;
  userId: string | null;
  actorUserId: string | null;
  email: string | null;
  sessionId: string | null;
  ipAddress: string | null;
  userAgent: string | null;
  correlationId: string | null;
  detail: string | null;
}

export interface MfaStatusDto {
  enabled: boolean;
  enabledAtUtc: string | null;
  recoveryCodesRemaining: number;
  requiredByWorkspace: boolean;
}

export interface MfaSetupDto {
  secret: string;
  otpAuthUri: string;
}

export interface TenantDto {
  id: string;
  name: string;
  slug: string;
  status: TenantStatus;
  createdAtUtc: string;
  updatedAtUtc: string;
}

export interface TenantOverviewDto {
  tenant: TenantDto;
  memberCount: number;
  branchCount: number;
  departmentCount: number;
}

export interface AddressDto {
  line1: string | null;
  line2: string | null;
  city: string | null;
  region: string | null;
  postalCode: string | null;
  countryCode: string | null;
}

export interface CompanyProfileDto {
  id: string;
  legalName: string;
  tradeName: string | null;
  taxId: string | null;
  registrationNumber: string | null;
  industry: string | null;
  website: string | null;
  contactEmail: string | null;
  contactPhone: string | null;
  timeZone: string;
  address: AddressDto;
  updatedAtUtc: string;
}

export interface BranchDto {
  id: string;
  name: string;
  code: string;
  isHeadquarters: boolean;
  phone: string | null;
  email: string | null;
  timeZone: string | null;
  isActive: boolean;
  address: AddressDto;
  createdAtUtc: string;
  updatedAtUtc: string;
}

export interface DepartmentDto {
  id: string;
  branchId: string;
  branchName: string;
  parentDepartmentId: string | null;
  name: string;
  code: string;
  description: string | null;
  isActive: boolean;
  createdAtUtc: string;
  updatedAtUtc: string;
}

export interface DepartmentTreeNode {
  id: string;
  name: string;
  code: string;
  isActive: boolean;
  children: DepartmentTreeNode[];
}

export interface PagedResult<T> {
  items: T[];
  page: number;
  pageSize: number;
  totalCount: number;
  totalPages: number;
}

/** RFC 9457 problem details as produced by the API's exception handler. */
export interface ProblemDetails {
  type?: string;
  title?: string;
  status?: number;
  detail?: string;
  instance?: string;
  code?: string;
  errors?: Record<string, string[]>;
  traceId?: string;
  correlationId?: string;
  requiredPermissions?: string[];
}

export const emptyAddress: AddressDto = {
  line1: null,
  line2: null,
  city: null,
  region: null,
  postalCode: null,
  countryCode: null,
};

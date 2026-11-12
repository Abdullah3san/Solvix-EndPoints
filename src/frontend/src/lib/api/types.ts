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

// ---------------------------------------------------------------- devices (Phase 04)

export type DeviceStatus = "Active" | "Retired";
export type DeviceConnectivity = "Online" | "Warning" | "Offline";
export type AgentHealthStatus = "Healthy" | "Degraded" | "Unhealthy";
export type EnrollmentTokenState = "Active" | "Expired" | "Exhausted" | "Revoked";
export type CredentialRevocationReason = "ReEnrolled" | "Superseded" | "DeviceRetired" | "Revoked";

export interface DeviceSummaryDto {
  id: string;
  hostname: string;
  displayName: string | null;
  platform: "Windows";
  status: DeviceStatus;
  connectivity: DeviceConnectivity;
  osName: string | null;
  osVersion: string | null;
  osDisplayVersion: string | null;
  osBuild: string | null;
  manufacturer: string | null;
  model: string | null;
  serialNumber: string | null;
  agentVersion: string;
  agentUpdateAvailable: boolean;
  healthStatus: AgentHealthStatus | null;
  ipAddresses: string[];
  assignedUserId: string | null;
  lastSeenAtUtc: string | null;
  lastInventoryAtUtc: string | null;
  enrolledAtUtc: string;
  branchId: string | null;
}

export interface HealthIssue {
  component: string;
  severity: "Info" | "Warning" | "Error";
  message: string;
}

export interface AgentHealthReport {
  status: AgentHealthStatus;
  startedAtUtc: string;
  uptimeSeconds: number;
  lastInventoryAtUtc: string | null;
  lastSuccessfulContactAtUtc: string | null;
  consecutiveFailures: number;
  workingSetBytes: number;
  certificateNotAfterUtc: string | null;
  osBuild: string | null;
  issues: HealthIssue[];
}

export interface DeviceCredentialDto {
  id: string;
  thumbprint: string;
  serialNumber: string;
  notBeforeUtc: string;
  notAfterUtc: string;
  issuedAtUtc: string;
  firstUsedAtUtc: string | null;
  revokedAtUtc: string | null;
  revocationReason: CredentialRevocationReason | null;
}

export interface DeviceDetailsDto {
  summary: DeviceSummaryDto;
  notes: string | null;
  hardwareId: string | null;
  domainName: string | null;
  architecture: string | null;
  processorName: string | null;
  totalMemoryBytes: number | null;
  lastBootAtUtc: string | null;
  macAddresses: string[];
  agentVersionChangedAtUtc: string | null;
  health: AgentHealthReport | null;
  groups: { id: string; name: string }[];
  assignment: {
    user: { id: string; displayName: string | null; email: string | null; isMember: boolean } | null;
    branch: { id: string; name: string | null; code: string | null } | null;
    department: { id: string; name: string | null; code: string | null } | null;
  };
  credentials: DeviceCredentialDto[];
  lastEnrolledAtUtc: string;
  enrollmentCount: number;
  inventoryRequested: boolean;
  retiredAtUtc: string | null;
  createdAtUtc: string;
  updatedAtUtc: string;
}

export interface InventoryReport {
  collectedAtUtc: string;
  agentVersion: string;
  hostname: string;
  system?: { manufacturer?: string; model?: string; serialNumber?: string; hardwareId?: string; systemSku?: string; chassisType?: string; domainName?: string; workgroup?: string } | null;
  bios?: { manufacturer?: string; version?: string; smbiosVersion?: string; serialNumber?: string; releaseDate?: string; firmwareType?: string; secureBootEnabled?: boolean } | null;
  operatingSystem?: {
    name?: string;
    family?: string;
    edition?: string;
    version?: string;
    displayVersion?: string;
    buildNumber?: string;
    updateBuildRevision?: number;
    architecture?: string;
    productType?: string;
    isSupported?: boolean;
    installDateUtc?: string;
    lastBootUpTimeUtc?: string;
    locale?: string;
    timeZone?: string;
  } | null;
  processors: { name?: string; manufacturer?: string; cores?: number; logicalProcessors?: number; maxClockSpeedMhz?: number; architecture?: string }[];
  memory?: { totalPhysicalBytes?: number; availablePhysicalBytes?: number; modules: { capacityBytes?: number; speedMhz?: number; manufacturer?: string; partNumber?: string; slot?: string }[] } | null;
  disks: { model?: string; serialNumber?: string; sizeBytes?: number; mediaType?: string; busType?: string; healthStatus?: string }[];
  volumes: { driveLetter?: string; label?: string; fileSystem?: string; sizeBytes?: number; freeBytes?: number; driveType?: string }[];
  networkAdapters: {
    name?: string;
    description?: string;
    macAddress?: string;
    adapterType?: string;
    speedBitsPerSecond?: number;
    status?: string;
    dhcpEnabled?: boolean;
    isPhysical: boolean;
    iPv4Addresses: string[];
    iPv6Addresses: string[];
    gateways: string[];
    dnsServers: string[];
  }[];
  software: { name: string; version?: string; publisher?: string; installDate?: string; scope?: string }[];
  collectionIssues: HealthIssue[];
}

export interface DeviceInventoryDto {
  deviceId: string;
  collectedAtUtc: string;
  receivedAtUtc: string;
  contentHash: string | null;
  report: InventoryReport;
}

export interface DeviceStatsDto {
  total: number;
  active: number;
  online: number;
  offline: number;
  warning: number;
  retired: number;
  updateAvailable: number;
  latestAgentVersion: string | null;
  agentVersions: { version: string; deviceCount: number; isLatest: boolean; updateAvailable: boolean }[];
}

export interface DeviceGroupDto {
  id: string;
  name: string;
  description: string | null;
  deviceCount: number;
  createdAtUtc: string;
  updatedAtUtc: string;
}

export interface EnrollmentTokenDto {
  id: string;
  name: string;
  state: EnrollmentTokenState;
  expiresAtUtc: string;
  maxUses: number;
  useCount: number;
  lastUsedAtUtc: string | null;
  revokedAtUtc: string | null;
  deviceGroupId: string | null;
  deviceGroupName: string | null;
  createdByUserId: string;
  createdAtUtc: string;
}

export interface CreatedEnrollmentTokenDto {
  enrollmentToken: EnrollmentTokenDto;
  token: string;
}

// ------------------------------------------------------------ monitoring (Phase 04)

export type NetworkStatus = "Unknown" | "Reachable" | "Degraded" | "Unreachable";
export type OsHealthStatus = "Unknown" | "Healthy" | "Warning" | "Critical";
export type MonitoringSignal = "Agent" | "Network" | "OperatingSystem";
export type ConnectionEventType =
  | "Enrolled"
  | "HeartbeatDelayed"
  | "HeartbeatRestored"
  | "WentOffline"
  | "Recovered"
  | "Reconnected"
  | "AgentRestarted"
  | "DeviceRebooted"
  | "NetworkStatusChanged"
  | "OsHealthChanged";
export type MonitoringAlertStatus = "Open" | "Resolved";

export interface MonitoringSettingsDto {
  heartbeatIntervalSeconds: number;
  warningAfterSeconds: number;
  offlineAfterSeconds: number;
  offlineAlertsEnabled: boolean;
  recoveryNotificationsEnabled: boolean;
  isDefault: boolean;
}

export interface StateCountsDto {
  total: number;
  online: number;
  warning: number;
  offline: number;
}

export interface BranchMonitoringDto {
  branchId: string | null;
  name: string;
  code: string | null;
  devices: StateCountsDto;
  availability7dPercent: number | null;
}

export interface OperatingSystemMonitoringDto {
  operatingSystem: string;
  devices: StateCountsDto;
}

export interface ConnectionEventDto {
  id: string;
  deviceId: string;
  deviceName: string;
  type: ConnectionEventType;
  signal: MonitoringSignal;
  occurredAtUtc: string;
  fromStatus: string | null;
  toStatus: string | null;
  detail: string | null;
  lastSeenAtUtc: string | null;
  durationSeconds: number | null;
  failedAttempts: number | null;
}

export interface MonitoringAlertDto {
  id: string;
  deviceId: string;
  deviceName: string;
  type: "DeviceOffline";
  status: MonitoringAlertStatus;
  message: string;
  openedAtUtc: string;
  lastSeenAtUtc: string;
  resolvedAtUtc: string | null;
  downtimeSeconds: number | null;
  acknowledgedAtUtc: string | null;
}

export interface MonitoringSummaryDto {
  generatedAtUtc: string;
  devices: StateCountsDto;
  retiredDevices: number;
  availability7dPercent: number | null;
  availability30dPercent: number | null;
  branches: BranchMonitoringDto[];
  operatingSystems: OperatingSystemMonitoringDto[];
  signals: { network: Record<NetworkStatus, number>; operatingSystem: Record<OsHealthStatus, number> };
  recentEvents: ConnectionEventDto[];
  openAlerts: number;
  settings: MonitoringSettingsDto;
}

export interface SignalDto<T> {
  status: T;
  checkedAtUtc: string | null;
  stale: boolean;
  issues: string[];
}

export interface AvailabilityTotalsDto {
  onlineSeconds: number;
  warningSeconds: number;
  offlineSeconds: number;
  offlineCount: number;
  availabilityPercent: number | null;
  downtimeSeconds: number;
  uptimeSeconds: number;
}

export interface DeviceMonitoringDto {
  deviceId: string;
  deviceName: string;
  monitored: boolean;
  connectivity: DeviceConnectivity;
  stateChangedAtUtc: string;
  lastSeenAtUtc: string;
  lastCommunicationAtUtc: string | null;
  lastHeartbeatAtUtc: string | null;
  onlineSinceUtc: string | null;
  currentUptimeSeconds: number | null;
  offlineSinceUtc: string | null;
  currentDowntimeSeconds: number | null;
  agentStartedAtUtc: string | null;
  systemBootedAtUtc: string | null;
  systemUptimeSeconds: number | null;
  network: SignalDto<NetworkStatus>;
  operatingSystem: SignalDto<OsHealthStatus>;
  today: AvailabilityTotalsDto;
  last7Days: AvailabilityTotalsDto;
  last30Days: AvailabilityTotalsDto;
  openAlert: MonitoringAlertDto | null;
  recentEvents: ConnectionEventDto[];
  thresholds: MonitoringSettingsDto;
}

export type AvailabilityGrouping = "Device" | "Branch";

export interface AvailabilityRowDto {
  id: string;
  name: string;
  branchName: string | null;
  operatingSystem: string | null;
  totals: AvailabilityTotalsDto;
}

export interface AvailabilityReportDto {
  from: string;
  to: string;
  generatedAtUtc: string;
  groupBy: AvailabilityGrouping;
  totals: AvailabilityTotalsDto;
  rows: AvailabilityRowDto[];
  days: { day: string; totals: AvailabilityTotalsDto }[];
}

export interface DeviceStatusChangeDto {
  deviceId: string;
  deviceName: string;
  branchId: string | null;
  from: DeviceConnectivity;
  to: DeviceConnectivity;
  changedAtUtc: string;
  lastSeenAtUtc: string;
}

export interface MonitoringUpdateDto {
  statusChanges: DeviceStatusChangeDto[];
  events: ConnectionEventDto[];
  alerts: MonitoringAlertDto[];
}

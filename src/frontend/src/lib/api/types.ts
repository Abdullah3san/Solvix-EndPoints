// DTOs mirroring the Solvix API (System.Text.Json web defaults: camelCase, enums as strings).

export type TenantStatus = "Active" | "Suspended" | "PendingDeletion";

/** Permission keys. Mirrors the API's permission catalog; authorization is always decided by the API. */
export type Permission =
  | "devices.view"
  | "devices.enroll"
  | "devices.manage"
  | "remote.view"
  | "remote.desktop"
  | "remote.desktop.control"
  | "remote.multimonitor"
  | "remote.terminal"
  | "remote.powershell"
  | "remote.execute_approved_commands"
  | "remote.file.upload"
  | "remote.file.download"
  | "remote.restart"
  | "remote.shutdown"
  | "remote.lock"
  | "remote.voice_call"
  | "remote.sessions.view"
  | "remote.sessions.terminate"
  | "remote.approvals.manage"
  | "software.view"
  | "software.packages.manage"
  | "software.deploy"
  | "software.approve"
  | "software.policies.manage"
  | "assets.view"
  | "assets.create"
  | "assets.edit"
  | "assets.transfer"
  | "mdm.manage"
  | "helpdesk.manage"
  | "posture.view"
  | "posture.manage"
  | "posture.approve"
  | "posture.approve_high"
  | "assistant.use"
  | "assistant.manage"
  | "assistant.approve_high"
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
  branchId: string | null;
  branchName: string | null;
  departmentId: string | null;
  departmentName: string | null;
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

// --- Software management (Phase 07) -------------------------------------------------------------

export type InstallerType = "Msi" | "Exe";
export type PackageStatus = "Ready" | "Corrupted" | "Retired";
export type SignatureState = "Unsigned" | "Signed";
export type SilentPreset = "Msi" | "InnoSetup" | "Nsis" | "WixBurn" | "InstallShield" | "Custom";
export type DetectionKind = "MsiProductCode" | "UninstallEntry";
export type NameMatch = "Exact" | "Prefix" | "Contains";
export type VersionOperator = "Any" | "Equal" | "AtLeast";
export type DeploymentAction = "Install" | "Uninstall";
export type DeploymentStatus =
  | "PendingApproval"
  | "Scheduled"
  | "Running"
  | "Completed"
  | "CompletedWithErrors"
  | "Failed"
  | "Cancelled"
  | "Rejected"
  | "Expired";
export type TargetStatus = "Pending" | "Dispatched" | "Running" | "Succeeded" | "Failed" | "Cancelled" | "Skipped" | "TimedOut" | "Expired";
export type SoftwareOutcome = "Installed" | "AlreadyInstalled" | "Uninstalled" | "NotInstalled" | "Detected" | "NotDetected" | "Failed";
export type RestrictionRuleType = "Blocked" | "Required";

export interface DetectionRule {
  kind: DetectionKind;
  productCode?: string | null;
  displayName?: string | null;
  nameMatch?: NameMatch;
  publisher?: string | null;
  version?: string | null;
  versionOperator?: VersionOperator;
}

export interface PackageDto {
  id: string;
  name: string;
  publisher: string | null;
  version: string | null;
  description: string | null;
  installerType: InstallerType;
  originalFileName: string;
  sizeBytes: number;
  sha256: string;
  status: PackageStatus;
  signatureState: SignatureState;
  signerSubject: string | null;
  signerThumbprint: string | null;
  signerTrusted: boolean;
  signatureContentVerified: boolean;
  architecture: string | null;
  isSensitive: boolean;
  silentPreset: SilentPreset;
  installArguments: string[];
  uninstallArguments: string[];
  successExitCodes: number[];
  rebootExitCodes: number[];
  timeoutMinutes: number;
  detection: DetectionRule | null;
  isConfigured: boolean;
  canUninstall: boolean;
  configurationRevision: number;
  uploadedBy: string;
  uploadedAtUtc: string;
  lastVerifiedAtUtc: string;
  retiredAtUtc: string | null;
  blockedByRule: string | null;
}

export interface ConfigurePackageRequest {
  name: string;
  publisher: string | null;
  version: string | null;
  description: string | null;
  isSensitive: boolean;
  silentPreset: SilentPreset;
  installArguments: string[] | null;
  uninstallArguments: string[] | null;
  successExitCodes: number[] | null;
  rebootExitCodes: number[] | null;
  timeoutMinutes: number;
  detection: DetectionRule;
}

export interface PackageVerification {
  packageId: string;
  intact: boolean;
  expectedSha256: string;
  actualSha256: string | null;
  status: PackageStatus;
}

export interface CreateDeploymentRequest {
  name: string;
  reason?: string | null;
  packageId: string;
  action: DeploymentAction;
  deploymentGroupId?: string | null;
  deviceIds?: string[];
  deviceGroupIds?: string[];
  branchIds?: string[];
  scheduledStartUtc?: string | null;
  deadlineUtc?: string | null;
  maxAttempts: number;
  retryDelayMinutes: number;
  skipConflicts: boolean;
}

export interface PlannedTargetDto {
  deviceId: string;
  deviceName: string;
  osName: string | null;
  connectivity: string;
  willRun: boolean;
  skipReason: string | null;
  conflictingDeploymentId: string | null;
}

export interface DeploymentPreviewDto {
  packageId: string;
  packageName: string;
  packageVersion: string | null;
  packageSha256: string;
  installerType: InstallerType;
  action: DeploymentAction;
  commandType: string;
  arguments: string[];
  detection: DetectionRule | null;
  requireSignature: boolean;
  timeoutMinutes: number;
  scheduledStartUtc: string;
  deadlineUtc: string;
  maxAttempts: number;
  targets: PlannedTargetDto[];
  deployableCount: number;
  conflictCount: number;
  isSensitive: boolean;
  sensitivityReasons: string[];
  requiredApprovals: number;
}

export interface TargetCountsDto {
  total: number;
  pending: number;
  inProgress: number;
  succeeded: number;
  failed: number;
  cancelled: number;
  skipped: number;
  timedOut: number;
  expired: number;
}

export interface DeploymentDto {
  id: string;
  name: string;
  reason: string | null;
  action: DeploymentAction;
  packageId: string;
  packageName: string;
  packageVersion: string | null;
  status: DeploymentStatus;
  createdBy: string;
  requestedAtUtc: string;
  scheduledStartUtc: string;
  deadlineUtc: string;
  isSensitive: boolean;
  sensitivityReasons: string[];
  requiredApprovals: number;
  approvalsReceived: number;
  approvalExpiresAtUtc: string;
  approvedAtUtc: string | null;
  startedAtUtc: string | null;
  completedAtUtc: string | null;
  cancelledBy: string | null;
  maxAttempts: number;
  counts: TargetCountsDto;
}

export interface DeploymentTargetDto {
  id: string;
  deviceId: string;
  deviceName: string;
  status: TargetStatus;
  outcome: SoftwareOutcome | null;
  attempts: number;
  currentExecutionId: string | null;
  nextAttemptAtUtc: string | null;
  exitCode: number | null;
  errorCode: string | null;
  errorMessage: string | null;
  rebootRequired: boolean;
  detectedVersion: string | null;
  logExcerpt: string | null;
  startedAtUtc: string | null;
  completedAtUtc: string | null;
}

export interface DeploymentApprovalDto {
  id: string;
  approverId: string;
  decision: "Approved" | "Rejected";
  comment: string | null;
  decidedAtUtc: string;
}

export interface DeploymentDetailDto {
  deployment: DeploymentDto;
  parameters: Record<string, string>;
  approvals: DeploymentApprovalDto[];
  targets: DeploymentTargetDto[];
}

export interface FailureSummary {
  errorCode: string;
  devices: number;
  example: string | null;
}

export interface DeploymentReport {
  deploymentId: string;
  name: string;
  status: DeploymentStatus;
  counts: TargetCountsDto;
  successRate: number;
  rebootRequired: number;
  averageDurationSeconds: number | null;
  failures: FailureSummary[];
  generatedAtUtc: string;
  final: boolean;
}

export interface DeploymentGroupDto {
  id: string;
  name: string;
  description: string | null;
  deviceIds: string[];
  deviceGroupIds: string[];
  branchIds: string[];
  excludedDeviceIds: string[];
  resolvedDeviceCount: number;
}

export interface DeploymentPolicyDto {
  allowExeInstallers: boolean;
  requireSignedPackages: boolean;
  trustedPublisherThumbprints: string[];
  requireApproval: boolean;
  largeDeploymentThreshold: number;
  uninstallIsSensitive: boolean;
  serverTargetsAreSensitive: boolean;
  approvalTtlHours: number;
}

export interface RestrictionRuleDto {
  id: string;
  name: string;
  description: string | null;
  ruleType: RestrictionRuleType;
  namePattern: string;
  publisher: string | null;
  minimumVersion: string | null;
  deviceGroupIds: string[] | null;
  enabled: boolean;
}

export interface ComplianceViolation {
  ruleId: string;
  ruleName: string;
  ruleType: RestrictionRuleType;
  deviceId: string;
  deviceName: string;
  applicationName: string | null;
  installedVersion: string | null;
  problem: string;
}

export interface ComplianceReport {
  devicesEvaluated: number;
  compliantDevices: number;
  violations: ComplianceViolation[];
}

export interface SoftwareCatalogEntryDto {
  name: string;
  publisher: string | null;
  deviceCount: number;
  versions: string[];
}

export interface DeviceSoftwareDto {
  deviceId: string;
  name: string;
  version: string | null;
  publisher: string | null;
  scope: string | null;
  installDate: string | null;
  firstSeenAtUtc: string;
  lastSeenAtUtc: string;
}

// ---------------------------------------------------------------- Asset management (Phase 05)

export type AssetCategory =
  | "Desktop"
  | "Laptop"
  | "Server"
  | "Printer"
  | "Monitor"
  | "MobilePhone"
  | "Tablet"
  | "ZebraHandheld"
  | "NetworkSwitch"
  | "Router"
  | "Firewall"
  | "AccessPoint"
  | "StorageDevice"
  | "Other";
export type AssetStatus = "InStock" | "InUse" | "InMaintenance" | "Missing" | "Retired" | "Disposed";
export type AssetCondition = "New" | "Good" | "Fair" | "Poor" | "Damaged";
export type DepreciationMethod = "None" | "StraightLine" | "DecliningBalance";
export type DisposalMethod = "Recycled" | "Sold" | "Donated" | "Destroyed" | "ReturnedToVendor" | "TradedIn" | "Lost";
export type WarrantyState = "None" | "Active" | "ExpiringSoon" | "Expired";
export type EndpointLinkState = "NotApplicable" | "Unlinked" | "Suggested" | "Linked";
export type MaintenanceType = "Preventive" | "Repair" | "Upgrade" | "Inspection" | "WarrantyClaim";
export type MaintenanceStatus = "Scheduled" | "InProgress" | "Completed" | "Cancelled";
export type PhysicalAuditStatus = "InProgress" | "Completed" | "Cancelled";
export type PhysicalAuditResult = "Pending" | "Found" | "Missing" | "Unexpected";
export type AssetDeviceLinkStatus = "Suggested" | "Approved" | "Rejected" | "Superseded" | "Unlinked";
export type AssetDeviceLinkSource = "Automatic" | "Manual";
export type MatchConfidence = "Low" | "Medium" | "High";
export type EvidenceOutcome = "Match" | "Partial" | "Mismatch" | "Unknown" | "Warning";
export type AuditActorKind = "User" | "System";
export type AssetSortField =
  | "AssetTag"
  | "Name"
  | "Category"
  | "Status"
  | "Manufacturer"
  | "Model"
  | "SerialNumber"
  | "PurchaseDate"
  | "PurchaseCost"
  | "WarrantyEndDate"
  | "LastAuditedAt"
  | "CreatedAt"
  | "UpdatedAt";
export type BulkAssetAction = "Transfer" | "Return" | "Retire" | "SetCondition" | "MarkMissing";

export interface RefDto {
  id: string;
  name: string | null;
  code?: string | null;
}

export interface AssigneeDto {
  userId: string | null;
  displayName: string | null;
  email: string | null;
  employeeNumber: string | null;
  isMember: boolean;
}

export interface AssetSummaryDto {
  id: string;
  assetTag: string;
  name: string;
  category: AssetCategory;
  status: AssetStatus;
  condition: AssetCondition;
  manufacturer: string | null;
  model: string | null;
  serialNumber: string | null;
  branch: RefDto | null;
  department: RefDto | null;
  location: string | null;
  assignee: AssigneeDto | null;
  vendor: RefDto | null;
  purchaseDate: string | null;
  purchaseCost: number | null;
  currency: string | null;
  warrantyEndDate: string | null;
  warranty: WarrantyState;
  endpointLink: EndpointLinkState;
  linkedDeviceId: string | null;
  lastAuditedAtUtc: string | null;
  createdAtUtc: string;
  updatedAtUtc: string;
}

export interface DepreciationYear {
  year: number;
  openingValue: number;
  depreciation: number;
  closingValue: number;
}

export interface DepreciationResult {
  method: DepreciationMethod;
  cost: number;
  salvageValue: number;
  usefulLifeMonths: number;
  startDate: string;
  asOf: string;
  monthsElapsed: number;
  accumulatedDepreciation: number;
  bookValue: number;
  currentMonthlyCharge: number;
  fullyDepreciatedOn: string;
  schedule: DepreciationYear[];
}

export interface EndpointDto {
  id: string;
  hostname: string;
  displayName: string | null;
  isActive: boolean;
  serialNumber: string | null;
  manufacturer: string | null;
  model: string | null;
  osName: string | null;
  connectivity: DeviceConnectivity;
  lastSeenAtUtc: string | null;
  enrolledAtUtc: string;
}

export interface LinkDiscrepancyDto {
  field: string;
  asset: string | null;
  endpoint: string | null;
}

export interface AssetEndpointLinkDto {
  linkId: string;
  endpoint: EndpointDto;
  source: AssetDeviceLinkSource;
  confidence: MatchConfidence;
  approvedAtUtc: string | null;
  discrepancies: LinkDiscrepancyDto[];
}

export interface MaintenanceRecordDto {
  id: string;
  assetId: string;
  assetTag: string | null;
  assetName: string | null;
  type: MaintenanceType;
  status: MaintenanceStatus;
  title: string;
  description: string | null;
  vendor: RefDto | null;
  performedBy: string | null;
  cost: number | null;
  scheduledFor: string | null;
  startedAtUtc: string | null;
  completedAtUtc: string | null;
  resolution: string | null;
  createdBy: RefDto | null;
  createdAtUtc: string;
}

export interface AssetDetailsDto {
  summary: AssetSummaryDto;
  version: number;
  description: string | null;
  purchaseOrderNumber: string | null;
  invoiceNumber: string | null;
  warrantyStartDate: string | null;
  warrantyProvider: string | null;
  warrantyReference: string | null;
  depreciationMethod: DepreciationMethod;
  usefulLifeMonths: number | null;
  salvageValue: number | null;
  depreciation: DepreciationResult | null;
  assignedAtUtc: string | null;
  retiredAtUtc: string | null;
  retirementReason: string | null;
  disposedAtUtc: string | null;
  disposalMethod: DisposalMethod | null;
  disposalValue: number | null;
  disposalReference: string | null;
  disposalNotes: string | null;
  endpointManageable: boolean;
  endpointLink: AssetEndpointLinkDto | null;
  pendingSuggestions: number;
  openMaintenance: MaintenanceRecordDto | null;
  qrPayload: string;
}

export interface AssetInput {
  assetTag?: string | null;
  name: string;
  category: AssetCategory;
  condition: AssetCondition;
  manufacturer: string | null;
  model: string | null;
  serialNumber: string | null;
  description: string | null;
  location: string | null;
  branchId: string | null;
  departmentId: string | null;
  vendorId: string | null;
  purchaseDate: string | null;
  purchaseCost: number | null;
  currency: string | null;
  purchaseOrderNumber: string | null;
  invoiceNumber: string | null;
  warrantyStartDate: string | null;
  warrantyEndDate: string | null;
  warrantyProvider: string | null;
  warrantyReference: string | null;
  depreciationMethod: DepreciationMethod;
  usefulLifeMonths: number | null;
  salvageValue: number | null;
}

export interface AssignAssetRequest {
  assignedUserId: string | null;
  assigneeName: string | null;
  employeeNumber: string | null;
  expectedReturnDate: string | null;
  notes: string | null;
}

export interface AssetStatsDto {
  total: number;
  inStock: number;
  inUse: number;
  inMaintenance: number;
  missing: number;
  retired: number;
  disposed: number;
  warrantyExpiringSoon: number;
  pendingEndpointMatches: number;
}

export interface AssignmentDto {
  id: string;
  assignee: AssigneeDto;
  branch: RefDto | null;
  department: RefDto | null;
  assignedAtUtc: string;
  assignedBy: RefDto | null;
  expectedReturnDate: string | null;
  notes: string | null;
  returnedAtUtc: string | null;
  returnedBy: RefDto | null;
  returnNotes: string | null;
  returnCondition: AssetCondition | null;
}

export interface TransferDto {
  id: string;
  fromBranch: RefDto | null;
  fromDepartment: RefDto | null;
  fromLocation: string | null;
  toBranch: RefDto | null;
  toDepartment: RefDto | null;
  toLocation: string | null;
  transferredAtUtc: string;
  transferredBy: RefDto | null;
  reason: string | null;
}

export interface AuditChangeDto {
  from: string | null;
  to: string | null;
}

export interface AuditLogEntryDto {
  id: string;
  occurredAtUtc: string;
  entityType: string;
  entityId: string;
  assetId: string | null;
  action: string;
  summary: string;
  changes: Record<string, AuditChangeDto> | null;
  actorKind: AuditActorKind;
  actor: RefDto | null;
  correlationId: string | null;
}

export interface MyAssetDto {
  id: string;
  assetTag: string;
  name: string;
  category: AssetCategory;
  status: AssetStatus;
  condition: AssetCondition;
  manufacturer: string | null;
  model: string | null;
  serialNumber: string | null;
  assignedAtUtc: string | null;
  warrantyEndDate: string | null;
  location: string | null;
}

export interface VendorDto {
  id: string;
  name: string;
  contactName: string | null;
  email: string | null;
  phone: string | null;
  website: string | null;
  accountNumber: string | null;
  notes: string | null;
  isActive: boolean;
  assetCount: number;
  createdAtUtc: string;
}

export interface PhysicalAuditDto {
  id: string;
  name: string;
  branch: RefDto | null;
  department: RefDto | null;
  category: AssetCategory | null;
  status: PhysicalAuditStatus;
  startedAtUtc: string;
  startedBy: RefDto | null;
  completedAtUtc: string | null;
  completedBy: RefDto | null;
  notes: string | null;
  expected: number;
  found: number;
  missing: number;
  pending: number;
  unexpected: number;
}

export interface PhysicalAuditItemDto {
  id: string;
  assetId: string;
  assetTag: string;
  assetName: string;
  category: AssetCategory;
  assetStatus: AssetStatus;
  expected: boolean;
  result: PhysicalAuditResult;
  expectedBranch: RefDto | null;
  expectedLocation: string | null;
  observedLocation: string | null;
  observedCondition: AssetCondition | null;
  scannedAtUtc: string | null;
  notes: string | null;
}

export interface ScanResultDto {
  item: PhysicalAuditItemDto;
  alreadyScanned: boolean;
  audit: PhysicalAuditDto;
}

export interface MatchEvidence {
  kind: string;
  outcome: EvidenceOutcome;
  detail: string;
}

export interface AssetRefDto {
  id: string;
  assetTag: string;
  name: string;
  category: AssetCategory;
  status: AssetStatus;
  manufacturer: string | null;
  model: string | null;
  serialNumber: string | null;
}

export interface AssetDeviceLinkDto {
  id: string;
  asset: AssetRefDto;
  endpoint: EndpointDto | null;
  status: AssetDeviceLinkStatus;
  source: AssetDeviceLinkSource;
  confidence: MatchConfidence;
  score: number;
  isAmbiguous: boolean;
  evidence: MatchEvidence[];
  discrepancies: LinkDiscrepancyDto[];
  detectedAtUtc: string;
  decidedAtUtc: string | null;
  decidedBy: RefDto | null;
  decisionNote: string | null;
}

export interface DeviceAssetDto {
  approved: AssetDeviceLinkDto | null;
  suggestions: AssetDeviceLinkDto[];
}

export interface RescanResultDto {
  endpointsEvaluated: number;
  suggestionsCreated: number;
  autoApproved: number;
}

export interface CountValueDto {
  key: string;
  label: string | null;
  count: number;
  cost: number;
}

export interface EndpointCoverageDto {
  manageableAssets: number;
  linkedAssets: number;
  pendingSuggestions: number;
  activeEndpoints: number;
  endpointsWithoutAsset: number;
}

export interface AssetSummaryReportDto {
  asOf: string;
  totalAssets: number;
  activeAssets: number;
  totalPurchaseCost: number;
  totalBookValue: number;
  byStatus: CountValueDto[];
  byCategory: CountValueDto[];
  byBranch: CountValueDto[];
  warrantyActive: number;
  warrantyExpiringSoon: number;
  warrantyExpired: number;
  warrantyNone: number;
  openMaintenance: number;
  maintenanceCostThisYear: number;
  notAuditedIn365Days: number;
  endpointCoverage: EndpointCoverageDto;
}

export interface DepreciationReportRowDto {
  category: AssetCategory;
  assets: number;
  cost: number;
  accumulatedDepreciation: number;
  bookValue: number;
}

export interface DepreciationReportDto {
  asOf: string;
  rows: DepreciationReportRowDto[];
  cost: number;
  accumulatedDepreciation: number;
  bookValue: number;
}

export interface AssetSettingsDto {
  tagPrefix: string;
  defaultCurrency: string;
  autoApproveHighConfidenceMatches: boolean;
  warrantyAlertDays: number;
}

// ---------------------------------------------------------------- AI assistant (Phase 11)

/** Assistant privilege level: Member = assistant.use, Admin = assistant.manage, Owner = assistant.approve_high. */
export type AssistantRole = "Member" | "Admin" | "Owner";
export type RiskLevel = "Low" | "Medium" | "High";
export type ProposalStatus = "PendingApproval" | "Executing" | "Succeeded" | "PartiallyFailed" | "Failed" | "Rejected" | "Expired";
export type AssistantTicketPriority = "Low" | "Normal" | "High" | "Urgent";
export type AssistantTicketStatus = "Open" | "InProgress" | "Resolved" | "Closed";
export type ReportKind = "FleetHealth" | "SoftwareInventory" | "Security" | "Performance";
export type DocumentStatus = "Processing" | "Ready" | "Failed";

export interface AssistantStatusDto {
  modelConfigured: boolean;
  model: string;
  embeddingModel: string;
  fleetConnected: boolean;
  commandsConnected: boolean;
  demoData: boolean;
  tools: string[];
  actions: string[];
}

export interface SourceDto {
  id: string;
  documentId: string;
  documentTitle: string;
  chunkId: string;
  ordinal: number;
  section: string | null;
  page: number | null;
  score: number;
  cited: boolean;
}

export interface ToolCallRecord {
  name: string;
  status: string;
  summary: string;
}

export interface ArtifactRef {
  type: "proposal" | "ticket" | "report" | string;
  id: string;
  label: string;
}

export interface MessageDto {
  id: string;
  role: "User" | "Assistant";
  content: string;
  sources: SourceDto[];
  toolCalls: ToolCallRecord[];
  artifacts: ArtifactRef[];
  createdAtUtc: string;
}

export interface ChatReplyDto {
  conversationId: string;
  message: MessageDto;
}

export interface ConversationDto {
  id: string;
  title: string;
  lastMessageAtUtc: string;
}

export interface ConversationDetailDto {
  id: string;
  title: string;
  messages: MessageDto[];
}

export interface ProposalTargetDto {
  id: string;
  name: string;
}

export interface ProposalResultDto {
  deviceId: string;
  status: string;
  message: string;
}

export interface ProposalDto {
  id: string;
  action: string;
  title: string;
  rationale: string;
  risk: RiskLevel;
  status: ProposalStatus;
  parameters: Record<string, string>;
  targets: ProposalTargetDto[];
  targetCount: number;
  proposedByEmail: string | null;
  createdAtUtc: string;
  expiresAtUtc: string;
  requiredApproverRole: AssistantRole;
  decidedByEmail: string | null;
  decidedAtUtc: string | null;
  decisionNote: string | null;
  executedAtUtc: string | null;
  results: ProposalResultDto[];
  autoApproved: boolean;
}

export interface KnowledgeDocumentDto {
  id: string;
  title: string;
  fileName: string;
  contentType: string;
  sizeBytes: number;
  status: DocumentStatus;
  failureReason: string | null;
  chunkCount: number;
  characterCount: number;
  redactionCount: number;
  embeddingModel: string;
  createdAtUtc: string;
}

export interface KnowledgeHit {
  documentId: string;
  documentTitle: string;
  chunkId: string;
  ordinal: number;
  section: string | null;
  page: number | null;
  score: number;
  content: string;
}

export interface TicketDto {
  id: string;
  reference: string;
  number: number;
  title: string;
  description: string;
  priority: AssistantTicketPriority;
  category: string | null;
  status: AssistantTicketStatus;
  devices: { id: string; name: string }[];
  createdByEmail: string | null;
  createdViaAssistant: boolean;
  createdAtUtc: string;
  resolvedAtUtc: string | null;
}

export interface ReportSummaryDto {
  id: string;
  title: string;
  kind: ReportKind;
  narrativeIncluded: boolean;
  generatedByEmail: string | null;
  createdAtUtc: string;
}

export interface ReportDto extends ReportSummaryDto {
  contentMarkdown: string;
  model: string | null;
}

export interface AuditEntryDto {
  id: string;
  kind: string;
  name: string;
  status: "Success" | "Denied" | "Failed";
  userEmail: string | null;
  conversationId: string | null;
  proposalId: string | null;
  input: string | null;
  summary: string | null;
  durationMs: number;
  correlationId: string | null;
  model: string | null;
  createdAtUtc: string;
}

// ------------------------------------------------------------------------------------------------ Helpdesk (Phase 12)

export type TicketStatus = "New" | "Open" | "InProgress" | "WaitingOnRequester" | "Resolved" | "Closed";
export type TicketPriority = "Low" | "Medium" | "High" | "Critical";
export type TicketSource = "Web" | "Email" | "Api";
export type SlaState = "none" | "ok" | "at_risk" | "breached" | "paused" | "met";

export interface TicketSummaryDto {
  id: string;
  number: number;
  subject: string;
  status: TicketStatus;
  priority: TicketPriority;
  source: TicketSource;
  categoryId: string | null;
  categoryName: string | null;
  suggestedCategoryId: string | null;
  needsTriage: boolean;
  requesterName: string | null;
  requesterEmail: string | null;
  assigneeTechnicianId: string | null;
  assigneeName: string | null;
  deviceId: string | null;
  firstResponseDueAtUtc: string | null;
  resolutionDueAtUtc: string | null;
  firstResponseBreached: boolean;
  resolutionBreached: boolean;
  slaState: SlaState;
  escalationLevel: number;
  createdAtUtc: string;
  lastActivityAtUtc: string;
}

export interface TicketCommentDto {
  id: string;
  authorName: string | null;
  authorEmail: string | null;
  isInternal: boolean;
  isFromStaff: boolean;
  body: string;
  source: TicketSource;
  createdAtUtc: string;
}

export interface TicketAttachmentDto {
  id: string;
  fileName: string;
  contentType: string;
  sizeBytes: number;
  sha256: string;
  createdAtUtc: string;
}

export interface TicketResolutionDto {
  id: string;
  notes: string;
  resolvedByTechnicianId: string | null;
  resolvedAtUtc: string;
  reopenedAtUtc: string | null;
}

export interface TicketDetailsDto {
  summary: TicketSummaryDto;
  description: string;
  deviceName: string | null;
  aiSummary: string | null;
  aiSummaryGeneratedAtUtc: string | null;
  aiSummarySource: string | null;
  reopenCount: number;
  comments: TicketCommentDto[];
  attachments: TicketAttachmentDto[];
  resolutions: TicketResolutionDto[];
}

export interface TicketEventDto {
  id: string;
  type: string;
  actorLabel: string | null;
  actorUserId: string | null;
  fromValue: string | null;
  toValue: string | null;
  detail: string | null;
  occurredAtUtc: string;
}

export interface TicketStatsDto {
  open: number;
  unassigned: number;
  needsTriage: number;
  escalated: number;
  overdue: number;
  resolvedLast7Days: number;
  dueSoon: number;
}

export interface TicketCategoryDto {
  id: string;
  name: string;
  description: string | null;
  isActive: boolean;
  defaultTechnicianId: string | null;
  keywords: string[];
}

export interface TechnicianDto {
  id: string;
  userId: string;
  displayName: string | null;
  email: string | null;
  isActive: boolean;
  isAvailable: boolean;
  maxOpenTickets: number | null;
  categoryIds: string[];
  notes: string | null;
  openTickets: number;
}

export interface SlaTargetDto {
  priority: TicketPriority;
  firstResponseMinutes: number;
  resolutionMinutes: number;
}

export interface SlaPolicyDto {
  id: string;
  name: string;
  isDefault: boolean;
  isActive: boolean;
  businessHoursOnly: boolean;
  timeZoneId: string;
  workdayStartMinute: number;
  workdayEndMinute: number;
  workDaysMask: number;
  targets: SlaTargetDto[];
}

export type EscalationTrigger = "FirstResponseElapsedPercent" | "ResolutionElapsedPercent";
export type EscalationAction = "FlagOnly" | "Reassign" | "RaisePriority";

export interface EscalationRuleDto {
  id: string;
  name: string;
  isActive: boolean;
  trigger: EscalationTrigger;
  thresholdPercent: number;
  action: EscalationAction;
  targetTechnicianId: string | null;
  minPriority: TicketPriority | null;
}

export interface InboundChannelDto {
  id: string;
  name: string;
  address: string | null;
  isEnabled: boolean;
  defaultPriority: TicketPriority;
  allowedSenderDomains: string[];
  lastReceivedAtUtc: string | null;
}

export interface CreatedInboundChannelDto {
  channel: InboundChannelDto;
  token: string;
}

// -------------------------------------------------------------------------------------------------- Billing (Phase 12)

export type BillingInterval = "Monthly" | "Annual";
export type SubscriptionStatus = "Trialing" | "Active" | "PastDue" | "Cancelled" | "Expired" | "Suspended";
export type InvoiceStatus = "Open" | "Paid" | "Void";

export interface PlanDto {
  id: string;
  code: string;
  name: string;
  description: string | null;
  deviceLimit: number | null;
  monthlyPriceMinor: number | null;
  annualPriceMinor: number | null;
  currency: string;
  isCustom: boolean;
}

export interface SubscriptionDto {
  planCode: string;
  planName: string;
  status: SubscriptionStatus;
  interval: BillingInterval;
  deviceLimit: number;
  unitPriceMinor: number;
  currency: string;
  trialEndsAtUtc: string | null;
  currentPeriodStartUtc: string;
  currentPeriodEndUtc: string;
  graceEndsAtUtc: string | null;
  cancelAtPeriodEnd: boolean;
  pendingPlanCode: string | null;
  pendingInterval: BillingInterval | null;
  canEnrollDevices: boolean;
  restrictionCode: string | null;
  restrictionMessage: string | null;
}

export interface UsageDto {
  devicesUsed: number;
  deviceLimit: number;
  percentUsed: number;
  level: "ok" | "warning" | "critical" | "limit";
  canEnrollDevices: boolean;
  history: { day: string; peakDevices: number; deviceLimit: number }[];
}

export interface InvoiceDto {
  id: string;
  number: string;
  status: InvoiceStatus;
  purpose: "NewSubscription" | "Renewal" | "PlanChange";
  currency: string;
  subtotalMinor: number;
  taxMinor: number;
  totalMinor: number;
  issuedAtUtc: string;
  dueAtUtc: string;
  paidAtUtc: string | null;
  periodStartUtc: string;
  periodEndUtc: string;
  planCode: string;
  interval: BillingInterval;
}

export interface ChangePlanResult {
  outcome: "invoice_issued" | "scheduled" | "unchanged";
  invoice: InvoiceDto | null;
  checkoutUrl: string | null;
  subscription: SubscriptionDto;
}

export interface CheckoutDto {
  invoice: InvoiceDto;
  checkoutUrl: string | null;
  provider: string;
}

export interface BillingProfileDto {
  legalName: string;
  billingEmail: string;
  addressLine1: string | null;
  addressLine2: string | null;
  city: string | null;
  region: string | null;
  postalCode: string | null;
  countryCode: string | null;
  taxId: string | null;
}

export interface BillingHistoryItemDto {
  kind: "invoice" | "payment" | "subscription";
  atUtc: string;
  title: string;
  amountMinor: number | null;
  currency: string | null;
  status: string | null;
  invoiceId: string | null;
}

// ------------------------------------------------------------------------------------------------- Reporting (Phase 12)

export interface ReportInfoDto {
  key: string;
  title: string;
  description: string;
  filters: string[];
}

export interface ReportColumnDto {
  key: string;
  label: string;
  type: "string" | "number" | "datetime" | "date" | "bool";
}

export interface ReportResultDto {
  report: string;
  title: string;
  generatedAtUtc: string;
  summary: { label: string; value: string | number | null }[];
  columns: ReportColumnDto[];
  rows: (string | number | boolean | null)[][];
  truncated: boolean;
  notes: string | null;
}

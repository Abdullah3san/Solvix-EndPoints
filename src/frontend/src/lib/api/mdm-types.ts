// DTOs for the mobile device management API (Phase 08). Enums are serialized as strings.

export type MobilePlatform = "Android" | "Ios";
export type DeviceOwnership = "CompanyOwned" | "PersonallyOwned";
export type ManagementMode = "FullyManaged" | "Dedicated" | "WorkProfile" | "DeviceEnrollment" | "AutomatedDeviceEnrollment" | "UserEnrollment";
export type DeviceState = "Pending" | "Enrolled" | "RemovalPending" | "Retired" | "Wiped";
export type ComplianceState = "Unknown" | "Compliant" | "NonCompliant";
export type IntegrityState = "Unknown" | "Intact" | "Compromised";
export type DeviceCommandType = "SyncInventory" | "ApplyPolicy" | "Lock" | "ResetPasscode" | "Reboot" | "InstallApp" | "RemoveApp" | "Retire" | "Wipe";
export type CommandState = "PendingApproval" | "Rejected" | "Queued" | "Sent" | "Acknowledged" | "Failed" | "Expired" | "Cancelled";
export type AppInstallType = "ForceInstalled" | "Available" | "Blocked" | "Kiosk";
export type PasswordQuality = "Required" | "Numeric" | "NumericComplex" | "Alphabetic" | "Alphanumeric" | "Complex";
export type WifiSecurity = "Open" | "WpaPersonal" | "WpaEnterprisePeap";
export type VpnType = "AlwaysOnApp" | "IkeV2";
export type AppControlMode = "BlockListOnly" | "ApprovedAppsOnly";

export interface Scenario {
  platform: MobilePlatform;
  mode: ManagementMode;
  ownership: DeviceOwnership;
}

export interface PasswordSettings {
  quality: PasswordQuality;
  minLength: number;
  maxFailedAttempts: number | null;
  maxInactivitySeconds: number | null;
  expirationDays: number | null;
  historyLength: number | null;
}

export interface WifiNetworkSettings {
  ssid: string;
  security: WifiSecurity;
  /** Write-only. The API returns null and sets passphraseSet when one is stored. */
  passphrase: string | null;
  passphraseSet: boolean;
  autoJoin: boolean;
  hidden: boolean;
  serverCaCertificatePem: string | null;
  serverNames: string[];
  anonymousIdentity: string | null;
}

export interface VpnSettings {
  name: string;
  type: VpnType;
  packageName: string | null;
  lockdown: boolean;
  server: string | null;
  remoteIdentifier: string | null;
  localIdentifier: string | null;
  /** Write-only. */
  sharedSecret: string | null;
  sharedSecretSet: boolean;
}

export interface RestrictionSettings {
  disableCamera: boolean;
  disableScreenCapture: boolean;
  disableUsbFileTransfer: boolean;
  disableUnknownSources: boolean;
  disableFactoryReset: boolean;
  blockManagedToPersonalSharing: boolean;
}

export interface ComplianceRequirements {
  minimumOsVersion: string | null;
  requirePasscode: boolean;
  requireEncryption: boolean;
  blockCompromisedDevices: boolean;
  maxHoursSinceCheckIn: number | null;
  requireAssignedPolicyApplied: boolean;
}

export interface KioskSettings {
  allowedPackages: string[];
  disableStatusBar: boolean;
  disableKeyguard: boolean;
  keepScreenOn: boolean;
}

export interface MobilePolicySettings {
  password: PasswordSettings | null;
  wifiNetworks: WifiNetworkSettings[];
  vpn: VpnSettings[];
  restrictions: RestrictionSettings;
  compliance: ComplianceRequirements;
  kiosk: KioskSettings | null;
  appControl: AppControlMode;
}

export interface PolicyAppDto {
  appId: string;
  name: string;
  identifier: string;
  installType: AppInstallType;
  managedConfigurationJson: string | null;
}

export interface PolicyDto extends Scenario {
  id: string;
  name: string;
  description: string | null;
  version: number;
  publishedVersion: number;
  publishedAtUtc: string | null;
  settings: MobilePolicySettings;
  apps: PolicyAppDto[];
  deviceCount: number;
  createdAtUtc: string;
  updatedAtUtc: string;
}

export interface PublishPolicyResultDto {
  policy: PolicyDto;
  applied: string[];
  skipped: string[];
  devicesNotified: number;
}

export interface AppDto {
  id: string;
  platform: MobilePlatform;
  name: string;
  identifier: string;
  source: "ManagedGooglePlay" | "AppStoreVpp";
  storeId: number | null;
  createdAtUtc: string;
}

export interface EnterpriseDto {
  platform: MobilePlatform;
  isBound: boolean;
  enterpriseName: string | null;
  displayName: string | null;
  boundAtUtc: string | null;
  lastSyncAtUtc: string | null;
  providerConfigured: boolean;
}

export interface EnrollmentDto extends Scenario {
  id: string;
  name: string;
  method: string;
  policyId: string;
  expiresAtUtc: string;
  maxUses: number;
  useCount: number;
  revokedAtUtc: string | null;
  isUsable: boolean;
  createdAtUtc: string;
}

export interface EnrollmentCreatedDto {
  enrollment: EnrollmentDto;
  qrPayload: string | null;
  tokenValue: string | null;
  enrollmentUrl: string | null;
  instructions: string[];
}

export interface DeviceDto extends Scenario {
  id: string;
  state: DeviceState;
  displayName: string | null;
  manufacturer: string | null;
  model: string | null;
  serialNumber: string | null;
  osVersion: string | null;
  securityPatchLevel: string | null;
  isEncrypted: boolean | null;
  hasPasscode: boolean | null;
  isSupervised: boolean | null;
  integrity: IntegrityState;
  policyId: string | null;
  appliedPolicyVersion: number | null;
  compliance: ComplianceState;
  complianceEvaluatedAtUtc: string | null;
  enrolledAtUtc: string | null;
  lastSeenAtUtc: string | null;
  retiredAtUtc: string | null;
  createdAtUtc: string;
}

export interface InstalledApp {
  identifier: string;
  name: string | null;
  version: string | null;
  isManaged: boolean;
}

export interface ComplianceViolation {
  rule: string;
  message: string;
}

export interface DeviceEventDto {
  id: string;
  type: string;
  summary: string;
  actorUserId: string | null;
  commandId: string | null;
  createdAtUtc: string;
}

export interface CapabilitiesDto extends Scenario {
  commands: DeviceCommandType[];
  commandsRequiringApproval: DeviceCommandType[];
  canRemoveCorporateDataOnly: boolean;
  canFactoryReset: boolean;
  canReadDeviceIdentifiers: boolean;
  canEnforceDeviceWidePolicy: boolean;
  limitations: string[];
}

export interface CommandDto {
  id: string;
  deviceId: string;
  type: DeviceCommandType;
  state: CommandState;
  requiresApproval: boolean;
  reason: string | null;
  requestedByUserId: string;
  decidedByUserId: string | null;
  decidedAtUtc: string | null;
  decisionNote: string | null;
  dispatchedAtUtc: string | null;
  completedAtUtc: string | null;
  expiresAtUtc: string;
  error: string | null;
  createdAtUtc: string;
}

export interface DeviceDetailDto {
  device: DeviceDto;
  apps: InstalledApp[];
  violations: ComplianceViolation[];
  events: DeviceEventDto[];
  commands: CommandDto[];
  capabilities: CapabilitiesDto;
}

export interface ComplianceBucketDto {
  key: string;
  total: number;
  compliant: number;
  nonCompliant: number;
  unknown: number;
}

export interface ComplianceSummaryDto {
  total: number;
  compliant: number;
  nonCompliant: number;
  unknown: number;
  byPlatform: ComplianceBucketDto[];
  byOwnership: ComplianceBucketDto[];
  topViolations: { rule: string; devices: number }[];
  pendingApprovals: number;
  generatedAtUtc: string;
}

export interface SyncResultDto {
  discovered: number;
  updated: number;
  removed: number;
  inventoryRequested: number;
  commandsReconciled: number;
  commandsExpired: number;
  devicesEvaluated: number;
}

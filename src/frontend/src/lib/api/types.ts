// DTOs mirroring the Solvix API (System.Text.Json web defaults: camelCase, enums as strings).

export type TenantRole = "Member" | "Admin" | "Owner";
export type TenantStatus = "Active" | "Suspended" | "PendingDeletion";

export interface UserDto {
  id: string;
  email: string;
  displayName: string;
}

export interface AuthTenantDto {
  id: string;
  slug: string;
  name: string;
  role: TenantRole;
}

export interface AuthResponse {
  accessToken: string;
  expiresAtUtc: string;
  user: UserDto;
  tenant: AuthTenantDto;
  availableTenants: AuthTenantDto[];
}

export interface CurrentUserDto {
  user: UserDto;
  tenant: AuthTenantDto;
}

export interface RegisterTenantResponse {
  tenantId: string;
  slug: string;
  name: string;
  auth: AuthResponse;
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

export interface MemberDto {
  membershipId: string;
  userId: string;
  email: string;
  displayName: string;
  role: TenantRole;
  isActive: boolean;
  joinedAtUtc: string;
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

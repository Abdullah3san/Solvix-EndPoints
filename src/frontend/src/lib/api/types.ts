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

// ---------------------------------------------------------------- devices (Phase 03)

export type DeviceStatus = "Active" | "Retired";
export type DeviceConnectivity = "Online" | "Offline";
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

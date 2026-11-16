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

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

// ---------------------------------------------------------------- AI assistant (Phase 11)

export type RiskLevel = "Low" | "Medium" | "High";
export type ProposalStatus = "PendingApproval" | "Executing" | "Succeeded" | "PartiallyFailed" | "Failed" | "Rejected" | "Expired";
export type TicketPriority = "Low" | "Normal" | "High" | "Urgent";
export type TicketStatus = "Open" | "InProgress" | "Resolved" | "Closed";
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
  requiredApproverRole: TenantRole;
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
  priority: TicketPriority;
  category: string | null;
  status: TicketStatus;
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

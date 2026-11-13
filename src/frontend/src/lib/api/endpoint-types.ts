// DTOs for Phase 09 (devices, patch management, configuration management, audit).

export type DeviceStatus = "Active" | "Retired";
export type TargetType = "AllDevices" | "Branch" | "Department" | "DeviceGroup" | "Device";
export type SettingRisk = "Low" | "Elevated" | "Destructive" | "Blocked";
export type EnforcementMode = "Monitor" | "Enforce";
export type RemovalBehavior = "Leave" | "Revert";
export type PatchApprovalState = "NotReviewed" | "Approved" | "Declined";
export type MsrcSeverity = "Unspecified" | "Low" | "Moderate" | "Important" | "Critical";
export type DeploymentStatus =
  | "Draft"
  | "Scheduled"
  | "InProgress"
  | "AwaitingPromotion"
  | "Halted"
  | "Paused"
  | "Completed"
  | "CompletedWithFailures"
  | "Cancelled";
export type DeploymentPhase = "NotStarted" | "Pilot" | "Broad";
export type RestartBehavior = "Never" | "WithinMaintenanceWindow" | "ForceAtDeadline";
export type PolicyVersionStatus = "Draft" | "PendingApproval" | "Approved" | "Rejected" | "Published" | "Superseded";
export type DeviceComplianceStatus = "Compliant" | "PendingRestart" | "NonCompliant" | "Unknown";
export type SettingComplianceStatus =
  | "Compliant"
  | "Drifted"
  | "Applied"
  | "Remediated"
  | "Failed"
  | "Refused"
  | "NotApplicable"
  | "Reverted"
  | "RevertFailed"
  | "RevertUnsupported";

export interface DeviceDto {
  id: string;
  hostname: string;
  dnsDomain: string | null;
  osName: string;
  osVersion: string;
  osBuild: string;
  agentVersion: string;
  branchId: string | null;
  departmentId: string | null;
  status: DeviceStatus;
  enrolledAtUtc: string;
  lastSeenAtUtc: string;
  lastBootUtc: string | null;
  groupIds: string[];
}

export interface DeviceGroupDto {
  id: string;
  name: string;
  description: string | null;
  memberCount: number;
  createdAtUtc: string;
}

export interface EnrollmentTokenDto {
  id: string;
  name: string;
  expiresAtUtc: string;
  maxUses: number | null;
  useCount: number;
  isRevoked: boolean;
  createdAtUtc: string;
}

export interface CreatedEnrollmentTokenDto {
  info: EnrollmentTokenDto;
  token: string;
}

export interface PatchDto {
  id: string;
  updateId: string;
  title: string;
  kbArticleIds: string[];
  cveIds: string[];
  classification: string;
  severity: MsrcSeverity;
  releaseDateUtc: string | null;
  rebootBehavior: string;
  approvalState: PatchApprovalState;
  autoApprovalRuleId: string | null;
  approvalNote: string | null;
  devicesMissing: number;
  devicesFailed: number;
  devicesPendingRestart: number;
  devicesInstalled: number;
}

export interface DevicePatchComplianceDto {
  deviceId: string;
  hostname: string;
  status: DeviceComplianceStatus;
  missingApproved: number;
  missingCritical: number;
  missingSecurity: number;
  failed: number;
  pendingRestart: number;
  rebootRequired: boolean;
  lastSuccessfulScanUtc: string | null;
}

export interface ComplianceBreakdownDto {
  id: string | null;
  name: string;
  devices: number;
  compliant: number;
  compliancePercent: number;
}

export interface PatchComplianceReportDto {
  generatedAtUtc: string;
  devices: number;
  compliant: number;
  pendingRestart: number;
  nonCompliant: number;
  unknown: number;
  compliancePercent: number;
  byBranch: ComplianceBreakdownDto[];
  byDepartment: ComplianceBreakdownDto[];
  byDeviceGroup: ComplianceBreakdownDto[];
  deviceRows: DevicePatchComplianceDto[];
}

export interface PatchFailureDto {
  deviceId: string;
  hostname: string;
  patchId: string;
  title: string;
  failureCount: number;
  lastFailureHex: string | null;
  lastFailureAtUtc: string | null;
  repeatedFailure: boolean;
}

export interface TargetDto {
  targetType: TargetType;
  targetId: string | null;
  isExclusion: boolean;
}

export interface DeploymentProgressDto {
  devices: number;
  pending: number;
  inFlight: number;
  pendingRestart: number;
  succeeded: number;
  failed: number;
  notApplicable: number;
  timedOut: number;
  cancelled: number;
  pilotDevices: number;
  pilotSettled: number;
  pilotSuccessPercent: number | null;
  failurePercent: number | null;
}

export interface DeploymentDto {
  id: string;
  name: string;
  status: DeploymentStatus;
  phase: DeploymentPhase;
  scheduledStartUtc: string;
  deadlineUtc: string | null;
  maintenanceWindowId: string | null;
  restartBehavior: RestartBehavior;
  pilotDeviceGroupId: string | null;
  pilotPercentage: number | null;
  pilotSuccessThresholdPercent: number;
  failureThresholdPercent: number;
  maxAttempts: number;
  patchIds: string[];
  targets: TargetDto[];
  haltReason: string | null;
  progress: DeploymentProgressDto;
  createdAtUtc: string;
}

export interface DeploymentJobDto {
  id: string;
  deviceId: string;
  hostname: string;
  ring: "Pilot" | "Broad";
  status: string;
  attempt: number;
  failureCount: number;
  stage: string | null;
  percentComplete: number;
  message: string | null;
  lastError: string | null;
  lastHResultHex: string | null;
}

export interface MaintenanceWindowDto {
  id: string;
  name: string;
  recurrence: "Weekly" | "Once";
  timeZoneId: string;
  daysOfWeek: string;
  startTime: string;
  durationMinutes: number;
  onceStartUtc: string | null;
  onceEndUtc: string | null;
  isEnabled: boolean;
  isOpenNow: boolean;
  nextOccurrenceStartsUtc: string | null;
}

export interface SettingView {
  key: string;
  kind: string;
  setting: Record<string, unknown>;
  risk: SettingRisk;
  riskReason: string;
  description: string;
}

export interface PolicyVersionDto {
  id: string;
  number: number;
  status: PolicyVersionStatus;
  maxRisk: SettingRisk;
  authorizedRisk: SettingRisk | null;
  changeNote: string | null;
  restoredFromNumber: number | null;
  createdAtUtc: string;
  reviewNote: string | null;
  destructiveAcknowledged: boolean;
  publishedAtUtc: string | null;
  approvalRequirement: string;
  settings: SettingView[];
}

export interface AssignmentDto {
  id: string;
  policyId: string;
  targetType: TargetType;
  targetId: string | null;
  isExclusion: boolean;
  mode: EnforcementMode;
  priority: number;
  maintenanceWindowId: string | null;
  removalBehavior: RemovalBehavior;
}

export interface ConfigurationPolicySummaryDto {
  id: string;
  name: string;
  description: string | null;
  isArchived: boolean;
  activeVersion: number | null;
  activeVersionRisk: SettingRisk | null;
  latestVersion: number;
  latestVersionStatus: PolicyVersionStatus;
  assignmentCount: number;
  updatedAtUtc: string;
}

export interface ConfigurationPolicyDto {
  id: string;
  name: string;
  description: string | null;
  isArchived: boolean;
  activeVersionId: string | null;
  versions: PolicyVersionDto[];
  assignments: AssignmentDto[];
}

export interface ConflictDto {
  key: string;
  isResolved: boolean;
  winningPolicyId: string | null;
  reason: string;
}

export interface DeviceConflictsDto {
  deviceId: string;
  hostname: string;
  conflicts: ConflictDto[];
}

export interface PolicyComplianceDto {
  policyId: string;
  policyName: string;
  devices: number;
  compliant: number;
  drifted: number;
  failed: number;
  pending: number;
}

export interface ConfigurationComplianceDto {
  devicesEvaluated: number;
  devicesCompliant: number;
  devicesWithDrift: number;
  devicesWithErrors: number;
  devicesPending: number;
  compliancePercent: number;
  policies: PolicyComplianceDto[];
}

export interface DriftEventDto {
  id: string;
  deviceId: string;
  settingKey: string;
  status: SettingComplianceStatus;
  expectedValue: string | null;
  actualValue: string | null;
  detectedAtUtc: string;
}

export interface BaselineDto {
  id: string;
  name: string;
  description: string;
  settings: SettingView[];
}

export interface AuditEntryDto {
  id: string;
  occurredAtUtc: string;
  actorType: "System" | "User" | "Agent";
  actorName: string | null;
  action: string;
  entityType: string;
  entityId: string | null;
  summary: string;
  correlationId: string | null;
}

export const riskTone: Record<SettingRisk, "success" | "warning" | "danger" | "neutral"> = {
  Low: "success",
  Elevated: "warning",
  Destructive: "danger",
  Blocked: "neutral",
};

export const deploymentTone: Record<DeploymentStatus, "neutral" | "brand" | "success" | "warning" | "danger"> = {
  Draft: "neutral",
  Scheduled: "brand",
  InProgress: "brand",
  AwaitingPromotion: "warning",
  Halted: "danger",
  Paused: "warning",
  Completed: "success",
  CompletedWithFailures: "warning",
  Cancelled: "neutral",
};

export const complianceTone: Record<DeviceComplianceStatus, "success" | "warning" | "danger" | "neutral"> = {
  Compliant: "success",
  PendingRestart: "warning",
  NonCompliant: "danger",
  Unknown: "neutral",
};

/** Actions an administrator can take on a deployment in its current state. */
export function deploymentActions(status: DeploymentStatus, phase: DeploymentPhase): string[] {
  switch (status) {
    case "Draft":
      return ["schedule"];
    case "Scheduled":
      return ["unschedule", "cancel"];
    case "InProgress":
      return phase === "Pilot" ? ["promote", "pause", "cancel"] : ["pause", "cancel"];
    case "AwaitingPromotion":
      return ["promote", "pause", "cancel"];
    case "Halted":
      return phase === "Pilot" ? ["force-promote", "cancel"] : ["resume", "cancel"];
    case "Paused":
      return ["resume", "cancel"];
    default:
      return [];
  }
}

/** Actions available on a configuration policy version. */
export function versionActions(status: PolicyVersionStatus): string[] {
  switch (status) {
    case "Draft":
      return ["submit"];
    case "PendingApproval":
      return ["approve", "reject"];
    case "Approved":
      return ["publish"];
    case "Superseded":
      return ["publish", "restore"];
    case "Published":
    case "Rejected":
      return ["restore"];
    default:
      return [];
  }
}

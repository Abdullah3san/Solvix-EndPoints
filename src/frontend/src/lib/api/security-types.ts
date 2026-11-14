/** Types for the Security module API (see docs/PHASE-10-SECURITY.md). Enums are serialised as strings. */

export type Severity = "Info" | "Low" | "Medium" | "High" | "Critical";
export type ComplianceBand = "Unknown" | "Compliant" | "AtRisk" | "NonCompliant";
export type CheckOutcome = "Pass" | "Fail" | "Unknown" | "NotApplicable";
export type FindingStatus = "Open" | "Resolved" | "Accepted";
export type AlertStatus = "New" | "Acknowledged" | "Resolved" | "Dismissed";
export type PolicyStatus = "Draft" | "PendingApproval" | "Active" | "Rejected" | "Retired";
export type EnforcementMode = "DetectOnly" | "ApprovalRequired" | "AutoApproveLowRisk";
export type UsbStoragePolicy = "Allow" | "BlockWrite" | "BlockAll";
export type RemediationRisk = "Low" | "Medium" | "High";
export type RemediationStatus = "PendingApproval" | "Approved" | "Rejected" | "Dispatched" | "Succeeded" | "Failed" | "Cancelled" | "Expired";
export type RuleCategory = "Defender" | "Firewall" | "Encryption" | "LocalAdmins" | "RemovableMedia" | "Software" | "Configuration" | "Events" | "Agent";

export interface Explanation {
  whyItMatters: string;
  severityRationale: string;
  howToFix: string;
  windowsInterface: string;
}

export interface EndpointSummary {
  id: string;
  hostname: string;
  status: "Active" | "Revoked";
  osCaption: string | null;
  osVersion: string | null;
  agentVersion: string | null;
  isDomainJoined: boolean | null;
  lastReportAtUtc: string | null;
  isStale: boolean;
  score: number | null;
  band: ComplianceBand;
  coverage: number | null;
  openCritical: number;
  openHigh: number;
  openMedium: number;
  openLow: number;
  keyIssuedAtUtc: string;
}

export interface CheckResult {
  ruleKey: string;
  subjectKey: string;
  category: RuleCategory;
  outcome: CheckOutcome;
  severity: Severity;
  title: string;
  evidence: string;
  remediationActionKey: string | null;
  remediationParameter: string | null;
  explanation: Explanation;
}

export interface ScorePenalty {
  ruleKey: string;
  title: string;
  severity: Severity;
  points: number;
  note: string;
}

export interface DeviceFacts {
  defender: {
    amRunningMode?: string | null;
    antivirusEnabled?: boolean | null;
    realTimeProtectionEnabled?: boolean | null;
    tamperProtected?: boolean | null;
    signatureAgeDays?: number | null;
    quickScanAgeDays?: number | null;
    activeThreatCount?: number | null;
    signatureVersion?: string | null;
    thirdPartyAntivirusRegistered?: boolean | null;
  } | null;
  firewall: { profiles?: { name: string; enabled: boolean | null; defaultInboundAction: string | null }[] | null } | null;
  bitLocker: {
    available?: boolean | null;
    tpmPresent?: boolean | null;
    volumes?: { mountPoint: string; volumeType: string | null; protectionOn: boolean | null; encryptionPercentage: number | null; encryptionMethod: string | null; volumeStatus: string | null }[] | null;
  } | null;
  localAdmins: {
    members?: { name: string; objectClass: string | null; sid: string | null }[] | null;
    builtInAdministratorEnabled?: boolean | null;
    guestAccountEnabled?: boolean | null;
  } | null;
  usb: { storageDriverDisabled?: boolean | null; denyWrite?: boolean | null; denyRead?: boolean | null } | null;
  effectiveUsbPolicy: UsbStoragePolicy | null;
  configuration: Record<string, boolean | null> | null;
  softwareCount: number | null;
  collectionErrors: { section: string; message: string }[];
}

export interface EndpointDetail {
  summary: EndpointSummary;
  policyName: string | null;
  policyVersion: number | null;
  scoreReasons: string[];
  scorePenalties: ScorePenalty[];
  checks: CheckResult[];
  facts: DeviceFacts | null;
}

export interface EndpointHistoryPoint {
  at: string;
  score: number;
  band: ComplianceBand;
}

export interface Enrollment {
  endpoint: EndpointSummary;
  deviceKey: string;
  notice: string;
}

export interface Finding {
  id: string;
  endpointId: string;
  endpointHostname: string;
  ruleKey: string;
  subjectKey: string;
  category: RuleCategory;
  status: FindingStatus;
  severity: Severity;
  title: string;
  evidence: string;
  firstDetectedAtUtc: string;
  lastDetectedAtUtc: string;
  resolvedAtUtc: string | null;
  recurrenceCount: number;
  acceptedUntilUtc: string | null;
  acceptanceReason: string | null;
  remediationActionKey: string | null;
  remediationParameter: string | null;
  explanation: Explanation;
}

export interface Alert {
  id: string;
  endpointId: string;
  endpointHostname: string;
  ruleKey: string;
  severity: Severity;
  status: AlertStatus;
  title: string;
  evidence: string;
  findingId: string | null;
  raisedAtUtc: string;
  acknowledgedAtUtc: string | null;
  closedAtUtc: string | null;
  closureNote: string | null;
  explanation: Explanation;
}

export interface PolicySettings {
  enforcementMode: EnforcementMode;
  allowedRemediationActions: string[];
  requireDefenderRealTimeProtection: boolean;
  requireDefenderTamperProtection: boolean;
  maxSignatureAgeDays: number;
  maxDaysSinceQuickScan: number;
  requireFirewallAllProfiles: boolean;
  requireBitLockerOsVolume: boolean;
  requireBitLockerFixedVolumes: boolean;
  maxLocalAdministrators: number;
  approvedLocalAdministrators: string[];
  requireBuiltInAdministratorDisabled: boolean;
  requireGuestAccountDisabled: boolean;
  usbStorage: UsbStoragePolicy;
  requireSmb1Disabled: boolean;
  requireUac: boolean;
  requireRdpNla: boolean;
  requireSecureBoot: boolean;
  requireWindowsUpdateNotDisabled: boolean;
  failedLogonThreshold: number;
  failedLogonWindowMinutes: number;
  staleAfterDays: number;
}

export interface Policy {
  id: string;
  version: number;
  name: string;
  description: string | null;
  status: PolicyStatus;
  settings: PolicySettings;
  authorUserId: string;
  submittedByUserId: string | null;
  submittedAtUtc: string | null;
  decidedByUserId: string | null;
  decidedAtUtc: string | null;
  decisionNote: string | null;
  activatedAtUtc: string | null;
  createdAtUtc: string;
}

export interface EffectivePolicy {
  policyId: string | null;
  version: number | null;
  name: string;
  isBuiltIn: boolean;
  settings: PolicySettings;
  advisoryCount: number;
}

export interface Advisory {
  id: string;
  title: string;
  productPattern: string;
  publisher: string | null;
  affectedFromVersion: string | null;
  fixedVersion: string;
  severity: Severity;
  reference: string | null;
  isEnabled: boolean;
  updatedAtUtc: string;
}

export interface Remediation {
  id: string;
  endpointId: string;
  endpointHostname: string;
  findingId: string | null;
  actionKey: string;
  actionTitle: string;
  parameter: string | null;
  risk: RemediationRisk;
  impact: string;
  status: RemediationStatus;
  reason: string;
  requestedByUserId: string | null;
  requestedBySystem: boolean;
  requestedAtUtc: string;
  expiresAtUtc: string;
  decidedByUserId: string | null;
  decidedAtUtc: string | null;
  decisionNote: string | null;
  autoApproved: boolean;
  dispatchedAtUtc: string | null;
  completedAtUtc: string | null;
  resultMessage: string | null;
  requiredApprover: string;
}

export interface AuditEntry {
  id: string;
  occurredAtUtc: string;
  actorType: "User" | "System" | "Device";
  actorId: string | null;
  actorLabel: string;
  action: string;
  entityType: string;
  entityId: string | null;
  summary: string;
  details: string | null;
  sourceIp: string | null;
  correlationId: string | null;
}

export interface SecurityEvent {
  id: string;
  endpointId: string;
  endpointHostname: string;
  log: string;
  recordId: number;
  eventId: number;
  provider: string | null;
  timeCreatedUtc: string;
  summary: string | null;
  targetGroup: string | null;
}

export interface SecuritySummary {
  generatedAtUtc: string;
  totalDevices: number;
  reportingDevices: number;
  staleDevices: number;
  averageScore: number | null;
  bands: Record<ComplianceBand, number>;
  openFindingsBySeverity: Record<Severity, number>;
  openFindingsByCategory: Record<string, number>;
  topRules: { ruleKey: string; title: string; severity: Severity; category: RuleCategory; affectedDevices: number }[];
  openAlerts: number;
  pendingRemediationApprovals: number;
  pendingPolicyApprovals: number;
  activePolicy: string;
  enforcementEnabled: boolean;
  trend: { day: string; averageScore: number; devices: number }[];
}

export interface RulesCatalog {
  rules: { key: string; title: string; category: RuleCategory; defaultSeverity: Severity; whatItChecks: string; whyItMatters: string; severityRationale: string; howToFix: string; remediationActionKey: string | null; windowsInterface: string }[];
  severities: { severity: Severity; meaning: string; example: string; scorePenalty: number }[];
  remediationActions: { key: string; title: string; description: string; risk: RemediationRisk; parameterName: string | null; allowedValues: string[]; windowsInterface: string; impact: string }[];
  scoring: { formula: string; compliantThreshold: number; nonCompliantThreshold: number; minimumCoverage: number; bandRules: string };
  productScope: string;
}

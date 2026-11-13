import type {
  AppInstallType,
  CommandState,
  ComplianceState,
  DeviceCommandType,
  DeviceOwnership,
  DeviceState,
  ManagementMode,
  MobilePlatform,
  MobilePolicySettings,
  Scenario,
} from "@/lib/api/mdm-types";

export type Tone = "neutral" | "brand" | "success" | "warning" | "danger";

export const platformLabel: Record<MobilePlatform, string> = { Android: "Android", Ios: "iOS / iPadOS" };
export const ownershipLabel: Record<DeviceOwnership, string> = { CompanyOwned: "Company-owned", PersonallyOwned: "Personal (BYOD)" };
export const modeLabel: Record<ManagementMode, string> = {
  FullyManaged: "Fully managed",
  Dedicated: "Dedicated (kiosk)",
  WorkProfile: "Work profile",
  DeviceEnrollment: "Device enrollment",
  AutomatedDeviceEnrollment: "Automated device enrollment",
  UserEnrollment: "User enrollment",
};
export const stateLabel: Record<DeviceState, string> = {
  Pending: "Pending",
  Enrolled: "Enrolled",
  RemovalPending: "Removal pending",
  Retired: "Retired",
  Wiped: "Wiped",
};
export const complianceLabel: Record<ComplianceState, string> = { Unknown: "Unknown", Compliant: "Compliant", NonCompliant: "Non-compliant" };
export const commandLabel: Record<DeviceCommandType, string> = {
  SyncInventory: "Refresh inventory",
  ApplyPolicy: "Apply policy",
  Lock: "Lock",
  ResetPasscode: "Reset passcode",
  Reboot: "Restart",
  InstallApp: "Install app",
  RemoveApp: "Remove app",
  Retire: "Retire (remove management)",
  Wipe: "Wipe (factory reset)",
};
export const commandStateLabel: Record<CommandState, string> = {
  PendingApproval: "Awaiting approval",
  Rejected: "Rejected",
  Queued: "Queued",
  Sent: "Sent",
  Acknowledged: "Completed",
  Failed: "Failed",
  Expired: "Expired",
  Cancelled: "Cancelled",
};
export const installTypeLabel: Record<AppInstallType, string> = {
  ForceInstalled: "Required (auto-install)",
  Available: "Available",
  Blocked: "Blocked",
  Kiosk: "Kiosk app",
};

export const complianceTone: Record<ComplianceState, Tone> = { Unknown: "neutral", Compliant: "success", NonCompliant: "danger" };
export const stateTone: Record<DeviceState, Tone> = { Pending: "neutral", Enrolled: "success", RemovalPending: "warning", Retired: "neutral", Wiped: "neutral" };
export const commandStateTone: Record<CommandState, Tone> = {
  PendingApproval: "warning",
  Rejected: "neutral",
  Queued: "brand",
  Sent: "brand",
  Acknowledged: "success",
  Failed: "danger",
  Expired: "neutral",
  Cancelled: "neutral",
};

/** Retire and wipe change management or destroy data, so the API holds them for a second administrator. */
export const destructiveCommands: ReadonlySet<DeviceCommandType> = new Set<DeviceCommandType>(["Retire", "Wipe"]);

/** Same list as the server's ManagementScenario.All; the API remains the authority and rejects anything else. */
export const scenarios: Scenario[] = [
  { platform: "Android", mode: "FullyManaged", ownership: "CompanyOwned" },
  { platform: "Android", mode: "Dedicated", ownership: "CompanyOwned" },
  { platform: "Android", mode: "WorkProfile", ownership: "CompanyOwned" },
  { platform: "Android", mode: "WorkProfile", ownership: "PersonallyOwned" },
  { platform: "Ios", mode: "AutomatedDeviceEnrollment", ownership: "CompanyOwned" },
  { platform: "Ios", mode: "DeviceEnrollment", ownership: "CompanyOwned" },
  { platform: "Ios", mode: "UserEnrollment", ownership: "PersonallyOwned" },
];

export const scenarioKey = (s: Scenario) => `${s.platform}|${s.mode}|${s.ownership}`;
export const scenarioLabel = (s: Scenario) => `${platformLabel[s.platform]} · ${modeLabel[s.mode]} · ${ownershipLabel[s.ownership]}`;

export function deviceTitle(d: { displayName: string | null; model: string | null; id: string }): string {
  return d.displayName ?? d.model ?? `Device ${d.id.slice(0, 8)}`;
}

export function emptySettings(): MobilePolicySettings {
  return {
    password: null,
    wifiNetworks: [],
    vpn: [],
    restrictions: {
      disableCamera: false,
      disableScreenCapture: false,
      disableUsbFileTransfer: false,
      disableUnknownSources: false,
      disableFactoryReset: false,
      blockManagedToPersonalSharing: false,
    },
    compliance: {
      minimumOsVersion: null,
      requirePasscode: false,
      requireEncryption: false,
      blockCompromisedDevices: false,
      maxHoursSinceCheckIn: null,
      requireAssignedPolicyApplied: false,
    },
    kiosk: null,
    appControl: "BlockListOnly",
  };
}

/** Parses a positive integer text field; empty or invalid input means "not set". */
export function intOrNull(value: string): number | null {
  const n = Number.parseInt(value, 10);
  return Number.isFinite(n) ? n : null;
}

export function formatDate(value: string | null | undefined): string {
  if (!value) return "—";
  const d = new Date(value);
  return Number.isNaN(d.getTime()) ? "—" : d.toLocaleString();
}

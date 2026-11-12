import type {
  AssetCategory,
  AssetCondition,
  AssetSortField,
  AssetStatus,
  DisposalMethod,
  EndpointLinkState,
  EvidenceOutcome,
  MaintenanceStatus,
  MaintenanceType,
  MatchConfidence,
  PhysicalAuditResult,
  WarrantyState,
} from "@/lib/api/types";

type Tone = "neutral" | "brand" | "success" | "warning" | "danger";

export const CATEGORIES: { value: AssetCategory; label: string; code: string; manageable: boolean }[] = [
  { value: "Desktop", label: "Desktop", code: "DSK", manageable: true },
  { value: "Laptop", label: "Laptop", code: "LAP", manageable: true },
  { value: "Server", label: "Server", code: "SRV", manageable: true },
  { value: "Printer", label: "Printer", code: "PRN", manageable: false },
  { value: "Monitor", label: "Monitor", code: "MON", manageable: false },
  { value: "MobilePhone", label: "Mobile phone", code: "MOB", manageable: false },
  { value: "Tablet", label: "Tablet", code: "TAB", manageable: true },
  { value: "ZebraHandheld", label: "Zebra handheld", code: "HHD", manageable: false },
  { value: "NetworkSwitch", label: "Network switch", code: "SWT", manageable: false },
  { value: "Router", label: "Router", code: "RTR", manageable: false },
  { value: "Firewall", label: "Firewall", code: "FWL", manageable: false },
  { value: "AccessPoint", label: "Access point", code: "WAP", manageable: false },
  { value: "StorageDevice", label: "Storage device", code: "STO", manageable: false },
  { value: "Other", label: "Other IT equipment", code: "OTH", manageable: false },
];

export const STATUSES: { value: AssetStatus; label: string }[] = [
  { value: "InStock", label: "In stock" },
  { value: "InUse", label: "In use" },
  { value: "InMaintenance", label: "In maintenance" },
  { value: "Missing", label: "Missing" },
  { value: "Retired", label: "Retired" },
  { value: "Disposed", label: "Disposed" },
];

export const CONDITIONS: AssetCondition[] = ["New", "Good", "Fair", "Poor", "Damaged"];

export const DISPOSAL_METHODS: { value: DisposalMethod; label: string }[] = [
  { value: "Recycled", label: "Recycled" },
  { value: "Sold", label: "Sold" },
  { value: "Donated", label: "Donated" },
  { value: "Destroyed", label: "Destroyed (data-bearing)" },
  { value: "ReturnedToVendor", label: "Returned to vendor" },
  { value: "TradedIn", label: "Traded in" },
  { value: "Lost", label: "Lost / written off" },
];

export const MAINTENANCE_TYPES: { value: MaintenanceType; label: string }[] = [
  { value: "Preventive", label: "Preventive" },
  { value: "Repair", label: "Repair" },
  { value: "Upgrade", label: "Upgrade" },
  { value: "Inspection", label: "Inspection" },
  { value: "WarrantyClaim", label: "Warranty claim" },
];

export const SORTABLE: { field: AssetSortField; label: string }[] = [
  { field: "AssetTag", label: "Asset tag" },
  { field: "Name", label: "Name" },
  { field: "Category", label: "Category" },
  { field: "Status", label: "Status" },
  { field: "PurchaseDate", label: "Purchased" },
  { field: "PurchaseCost", label: "Cost" },
  { field: "WarrantyEndDate", label: "Warranty" },
];

export function categoryLabel(category: AssetCategory): string {
  return CATEGORIES.find((c) => c.value === category)?.label ?? category;
}

export function statusLabel(status: AssetStatus): string {
  return STATUSES.find((s) => s.value === status)?.label ?? status;
}

export function statusTone(status: AssetStatus): Tone {
  switch (status) {
    case "InUse":
      return "success";
    case "InStock":
      return "brand";
    case "InMaintenance":
      return "warning";
    case "Missing":
      return "danger";
    default:
      return "neutral";
  }
}

export function conditionTone(condition: AssetCondition): Tone {
  return condition === "Damaged" || condition === "Poor" ? "danger" : condition === "Fair" ? "warning" : "neutral";
}

export function warrantyTone(state: WarrantyState): Tone {
  return state === "Active" ? "success" : state === "ExpiringSoon" ? "warning" : state === "Expired" ? "danger" : "neutral";
}

export function warrantyLabel(state: WarrantyState): string {
  return state === "ExpiringSoon" ? "Expiring soon" : state === "None" ? "No warranty" : state;
}

export function linkLabel(state: EndpointLinkState): string {
  switch (state) {
    case "Linked":
      return "Managed endpoint";
    case "Suggested":
      return "Review match";
    case "Unlinked":
      return "No agent";
    default:
      return "Not applicable";
  }
}

export function linkTone(state: EndpointLinkState): Tone {
  return state === "Linked" ? "success" : state === "Suggested" ? "warning" : "neutral";
}

export function confidenceTone(confidence: MatchConfidence): Tone {
  return confidence === "High" ? "success" : confidence === "Medium" ? "warning" : "danger";
}

export function evidenceTone(outcome: EvidenceOutcome): Tone {
  switch (outcome) {
    case "Match":
      return "success";
    case "Partial":
      return "brand";
    case "Mismatch":
      return "danger";
    case "Warning":
      return "warning";
    default:
      return "neutral";
  }
}

export function auditResultTone(result: PhysicalAuditResult): Tone {
  return result === "Found" ? "success" : result === "Missing" ? "danger" : result === "Unexpected" ? "warning" : "neutral";
}

export function maintenanceTone(status: MaintenanceStatus): Tone {
  return status === "InProgress" ? "warning" : status === "Scheduled" ? "brand" : status === "Completed" ? "success" : "neutral";
}

export const EVIDENCE_LABELS: Record<string, string> = {
  serialNumber: "Serial number",
  manufacturer: "Manufacturer",
  model: "Model",
  category: "Category",
  ambiguity: "Ambiguity",
  branch: "Branch",
  assignedUser: "Assigned user",
};

/** Formats an ISO date-only value ("2026-01-15") without time-zone shifting. */
export function formatDay(value: string | null | undefined): string {
  if (!value) return "—";
  const match = /^(\d{4})-(\d{2})-(\d{2})/.exec(value);
  if (!match) return "—";
  const date = new Date(Number(match[1]), Number(match[2]) - 1, Number(match[3]));
  return new Intl.DateTimeFormat(undefined, { dateStyle: "medium" }).format(date);
}

export function formatMoney(value: number | null | undefined, currency?: string | null): string {
  if (value === null || value === undefined || !Number.isFinite(value)) return "—";
  if (currency && /^[A-Z]{3}$/.test(currency)) {
    try {
      return new Intl.NumberFormat(undefined, { style: "currency", currency }).format(value);
    } catch {
      // fall through to plain formatting for unknown codes
    }
  }
  return new Intl.NumberFormat(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 }).format(value);
}

export function assigneeLabel(assignee: { displayName: string | null; email: string | null; employeeNumber: string | null; isMember: boolean; userId: string | null } | null): string {
  if (!assignee) return "—";
  const name = assignee.displayName ?? (assignee.userId ? "Former member" : "—");
  return assignee.employeeNumber ? `${name} (${assignee.employeeNumber})` : name;
}

export interface RegisterFilters {
  search: string;
  category: AssetCategory | "";
  status: AssetStatus | "";
  condition: AssetCondition | "";
  branchId: string;
  departmentId: string;
  vendorId: string;
  warranty: WarrantyState | "";
  endpointLink: EndpointLinkState | "";
  assigned: "" | "true" | "false";
  sort: AssetSortField;
  direction: "Asc" | "Desc";
  pageSize: number;
}

export const DEFAULT_FILTERS: RegisterFilters = {
  search: "",
  category: "",
  status: "",
  condition: "",
  branchId: "",
  departmentId: "",
  vendorId: "",
  warranty: "",
  endpointLink: "",
  assigned: "",
  sort: "AssetTag",
  direction: "Asc",
  pageSize: 25,
};

/** Builds the API query (and the shareable URL) for the register; defaults are omitted. */
export function registerQuery(filters: RegisterFilters, page?: number): URLSearchParams {
  const params = new URLSearchParams();
  for (const key of Object.keys(DEFAULT_FILTERS) as (keyof RegisterFilters)[]) {
    const value = filters[key];
    if (value !== "" && value !== DEFAULT_FILTERS[key]) params.set(key, String(value));
  }
  if (page && page > 1) params.set("page", String(page));
  return params;
}

/** Parses the register URL; unknown or malformed values fall back to defaults. */
export function parseRegisterQuery(params: Record<string, string | string[] | undefined>): RegisterFilters & { page: number } {
  const get = (key: string) => {
    const value = params[key];
    return typeof value === "string" ? value : "";
  };
  const oneOf = <T extends string>(value: string, allowed: readonly T[], fallback: T | ""): T | "" => (allowed.includes(value as T) ? (value as T) : fallback);
  const uuid = (value: string) => (/^[0-9a-f-]{36}$/i.test(value) ? value : "");
  const pageSize = Number(get("pageSize"));
  const page = Number(get("page"));
  return {
    search: get("search").slice(0, 200),
    category: oneOf(get("category"), CATEGORIES.map((c) => c.value), ""),
    status: oneOf(get("status"), STATUSES.map((s) => s.value), ""),
    condition: oneOf(get("condition"), CONDITIONS, ""),
    branchId: uuid(get("branchId")),
    departmentId: uuid(get("departmentId")),
    vendorId: uuid(get("vendorId")),
    warranty: oneOf(get("warranty"), ["None", "Active", "ExpiringSoon", "Expired"] as const, ""),
    endpointLink: oneOf(get("endpointLink"), ["NotApplicable", "Unlinked", "Suggested", "Linked"] as const, ""),
    assigned: oneOf(get("assigned"), ["true", "false"] as const, ""),
    sort: (oneOf(get("sort"), SORTABLE.map((s) => s.field).concat(["Manufacturer", "Model", "SerialNumber", "LastAuditedAt", "CreatedAt", "UpdatedAt"]), "") || "AssetTag") as AssetSortField,
    direction: get("direction") === "Desc" ? "Desc" : "Asc",
    pageSize: [10, 25, 50, 100].includes(pageSize) ? pageSize : 25,
    page: Number.isInteger(page) && page > 0 ? page : 1,
  };
}

/** Counts filters other than search, sort and page size (for the "Filters (n)" badge). */
export function activeFilterCount(filters: RegisterFilters): number {
  return (["category", "status", "condition", "branchId", "departmentId", "vendorId", "warranty", "endpointLink", "assigned"] as const).filter((k) => filters[k] !== "").length;
}

export function auditActionLabel(action: string): string {
  const labels: Record<string, string> = {
    "asset.registered": "Registered",
    "asset.updated": "Updated",
    "asset.assigned": "Assigned",
    "asset.returned": "Checked in",
    "asset.transferred": "Transferred",
    "asset.retired": "Retired",
    "asset.reinstated": "Reinstated",
    "asset.disposed": "Disposed",
    "asset.missing": "Marked missing",
    "asset.found": "Found",
    "asset.condition_changed": "Condition changed",
    "maintenance.scheduled": "Maintenance scheduled",
    "maintenance.started": "Maintenance started",
    "maintenance.completed": "Maintenance completed",
    "maintenance.cancelled": "Maintenance cancelled",
    "endpoint_match.suggested": "Endpoint match suggested",
    "endpoint_match.withdrawn": "Endpoint match withdrawn",
    "endpoint_match.auto_approved": "Endpoint linked automatically",
    "endpoint_link.approved": "Endpoint match approved",
    "endpoint_link.rejected": "Endpoint match rejected",
    "endpoint_link.linked": "Endpoint linked manually",
    "endpoint_link.unlinked": "Endpoint unlinked",
    "endpoint_match.rescan": "Endpoints rescanned",
    "physical_audit.started": "Audit started",
    "physical_audit.completed": "Audit completed",
    "physical_audit.cancelled": "Audit cancelled",
    "vendor.created": "Vendor added",
    "vendor.updated": "Vendor updated",
    "vendor.deleted": "Vendor deleted",
    "settings.updated": "Settings updated",
  };
  return labels[action] ?? action;
}

import { beforeEach, describe, expect, it, vi } from "vitest";
import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import type { AssetDeviceLinkDto, AssetStatsDto, AssetSummaryDto, PagedResult } from "@/lib/api/types";
import { DEFAULT_FILTERS } from "@/lib/assets";
import { ToastProvider } from "@/components/providers/toast-provider";
import { AssetRegister } from "@/components/features/assets/asset-register";
import { MatchingView } from "@/components/features/assets/matching-view";

const api = vi.hoisted(() => ({ get: vi.fn(), post: vi.fn(), put: vi.fn(), patch: vi.fn(), delete: vi.fn() }));
vi.mock("@/lib/api/client", () => ({ api, PROXY_PREFIX: "/api/proxy/v1" }));
vi.mock("next/navigation", () => ({ useRouter: () => ({ refresh: vi.fn(), push: vi.fn() }) }));

function asset(n: number, patch: Partial<AssetSummaryDto> = {}): AssetSummaryDto {
  return {
    id: `00000000-0000-0000-0000-00000000000${n}`,
    assetTag: `AST-LAP-00000${n}`,
    name: `Laptop ${n}`,
    category: "Laptop",
    status: "InStock",
    condition: "Good",
    manufacturer: "Dell Inc.",
    model: "Latitude 7440",
    serialNumber: `SN${n}`,
    branch: null,
    department: null,
    location: null,
    assignee: null,
    vendor: null,
    purchaseDate: null,
    purchaseCost: 1000 * n,
    currency: "USD",
    warrantyEndDate: null,
    warranty: "None",
    endpointLink: "Unlinked",
    linkedDeviceId: null,
    lastAuditedAtUtc: null,
    createdAtUtc: "2026-11-01T00:00:00Z",
    updatedAtUtc: "2026-11-01T00:00:00Z",
    ...patch,
  };
}

const page = (items: AssetSummaryDto[]): PagedResult<AssetSummaryDto> => ({ items, page: 1, pageSize: 25, totalCount: items.length, totalPages: 1 });
const stats: AssetStatsDto = { total: 2, inStock: 2, inUse: 0, inMaintenance: 0, missing: 0, retired: 0, disposed: 0, warrantyExpiringSoon: 0, pendingEndpointMatches: 3 };

function renderRegister(items = [asset(1), asset(2, { status: "InUse", assignee: { userId: null, displayName: "Nadia", email: null, employeeNumber: "E-9", isMember: false } })]) {
  return render(
    <ToastProvider>
      <AssetRegister initial={page(items)} initialFilters={DEFAULT_FILTERS} stats={stats} branches={[]} departments={[]} vendors={[]} />
    </ToastProvider>,
  );
}

describe("asset register", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    window.history.replaceState(null, "", "/assets");
  });

  it("lists assets with holder, status, endpoint state and pending matches", () => {
    renderRegister();
    expect(screen.getByRole("link", { name: "AST-LAP-000001" })).toHaveAttribute("href", "/assets/00000000-0000-0000-0000-000000000001");
    expect(screen.getByText("Nadia (E-9)")).toBeInTheDocument();
    expect(screen.getAllByText("No agent")).toHaveLength(2);
    expect(screen.getByRole("link", { name: /3 managed endpoint matches are waiting/i })).toHaveAttribute("href", "/assets/matching");
    expect(screen.getByRole("link", { name: /export csv/i })).toHaveAttribute("href", "/api/proxy/v1/assets/export?");
  });

  it("sorts through the API and keeps the URL shareable", async () => {
    api.get.mockResolvedValue(page([asset(2), asset(1)]));
    renderRegister();
    await userEvent.click(screen.getByRole("button", { name: /^cost/i }));
    await waitFor(() => expect(api.get).toHaveBeenCalledWith("assets", expect.objectContaining({ sort: "PurchaseCost", pageSize: 25, page: 1 })));
    expect(window.location.search).toBe("?sort=PurchaseCost");
    await userEvent.click(screen.getByRole("button", { name: /^cost/i }));
    await waitFor(() => expect(api.get).toHaveBeenLastCalledWith("assets", expect.objectContaining({ sort: "PurchaseCost", direction: "Desc" })));
    expect(screen.getByRole("columnheader", { name: /cost/i })).toHaveAttribute("aria-sort", "descending");
  });

  it("selects rows and runs an all-or-nothing bulk retire", async () => {
    api.post.mockResolvedValue({ processed: 2 });
    api.get.mockResolvedValue(page([]));
    renderRegister();
    expect(screen.queryByRole("toolbar", { name: /bulk actions/i })).not.toBeInTheDocument();
    await userEvent.click(screen.getByRole("checkbox", { name: /select all assets on this page/i }));
    const toolbar = screen.getByRole("toolbar", { name: /bulk actions/i });
    expect(within(toolbar).getByText("2 selected")).toBeInTheDocument();
    expect(within(toolbar).getByRole("link", { name: /print labels/i })).toHaveAttribute(
      "href",
      "/assets/labels?ids=00000000-0000-0000-0000-000000000001,00000000-0000-0000-0000-000000000002",
    );

    await userEvent.click(within(toolbar).getByRole("button", { name: /retire/i }));
    const dialog = screen.getByRole("dialog");
    const confirm = within(dialog).getByRole("button", { name: /^retire$/i });
    expect(confirm).toBeDisabled();
    await userEvent.type(within(dialog).getByLabelText(/reason/i), "End of lease");
    await userEvent.click(confirm);
    await waitFor(() =>
      expect(api.post).toHaveBeenCalledWith("assets/bulk", {
        assetIds: ["00000000-0000-0000-0000-000000000001", "00000000-0000-0000-0000-000000000002"],
        action: "Retire",
        reason: "End of lease",
      }),
    );
    await waitFor(() => expect(screen.queryByRole("toolbar", { name: /bulk actions/i })).not.toBeInTheDocument());
  });
});

describe("endpoint matching review", () => {
  const suggestion: AssetDeviceLinkDto = {
    id: "link-1",
    asset: { id: "a1", assetTag: "AST-LAP-000001", name: "Laptop 1", category: "Laptop", status: "InUse", manufacturer: "Dell Inc.", model: "Latitude 7440", serialNumber: "5CD1234" },
    endpoint: { id: "d1", hostname: "PC-001", displayName: null, isActive: true, serialNumber: "5cd1234", manufacturer: "DELL", model: "Latitude 7440", osName: "Windows 11 Pro", connectivity: "Online", lastSeenAtUtc: null, enrolledAtUtc: "2026-11-01T00:00:00Z" },
    status: "Suggested",
    source: "Automatic",
    confidence: "High",
    score: 100,
    isAmbiguous: true,
    evidence: [
      { kind: "serialNumber", outcome: "Match", detail: "5CD1234" },
      { kind: "ambiguity", outcome: "Warning", detail: "2 assets share this serial number." },
    ],
    discrepancies: [],
    detectedAtUtc: "2026-11-12T10:00:00Z",
    decidedAtUtc: null,
    decidedBy: null,
    decisionNote: null,
  };

  beforeEach(() => vi.clearAllMocks());

  it("shows evidence, flags ambiguity and approves only after confirmation", async () => {
    api.post.mockResolvedValue({ ...suggestion, status: "Approved" });
    api.get.mockImplementation((path: string) =>
      Promise.resolve(path === "asset-matches" ? { items: [], page: 1, pageSize: 25, totalCount: 0, totalPages: 0 } : { items: [], page: 1, pageSize: 25, totalCount: 0, totalPages: 0 }),
    );
    render(
      <ToastProvider>
        <MatchingView initial={{ items: [suggestion], page: 1, pageSize: 25, totalCount: 1, totalPages: 1 }} unregistered={{ items: [], page: 1, pageSize: 25, totalCount: 0, totalPages: 0 }} />
      </ToastProvider>,
    );
    expect(screen.getByText(/ambiguous — review carefully/i)).toBeInTheDocument();
    expect(screen.getByText("High confidence")).toBeInTheDocument();
    const evidence = screen.getByRole("list", { name: /evidence/i });
    expect(within(evidence).getByText("Serial number")).toBeInTheDocument();
    expect(within(evidence).getByText("2 assets share this serial number.")).toBeInTheDocument();

    await userEvent.click(screen.getByRole("button", { name: /^approve$/i }));
    const dialog = screen.getByRole("dialog");
    expect(within(dialog).getByText(/several records share this serial number/i)).toBeInTheDocument();
    expect(api.post).not.toHaveBeenCalled();
    await userEvent.type(within(dialog).getByLabelText(/note/i), "Label checked");
    await userEvent.click(within(dialog).getByRole("button", { name: /^approve$/i }));
    await waitFor(() => expect(api.post).toHaveBeenCalledWith("asset-matches/link-1/approve", { note: "Label checked" }));
  });
});

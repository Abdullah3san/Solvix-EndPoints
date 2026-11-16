import { describe, expect, it, vi } from "vitest";
import { render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { Monitor } from "lucide-react";
import * as React from "react";
import { ConfirmDialog } from "@/components/ui/confirm-dialog";
import { DataTable, DataTableColumnHeader, DataTableViewOptions, FacetedFilter, type ColumnDef, type RowSelectionState, type SortingState, type VisibilityState } from "@/components/ui/data-table";
import { EmptyState } from "@/components/ui/empty-state";
import { MetricCard } from "@/components/ui/metric-card";
import { pageRange, Pagination } from "@/components/ui/pagination";

vi.mock("next/link", () => ({ default: ({ href, children, ...props }: { href: string; children: React.ReactNode }) => <a href={href} {...props}>{children}</a> }));

describe("MetricCard", () => {
  it("formats the value and links to the underlying list", () => {
    render(<MetricCard label="Managed devices" value={1284} icon={Monitor} caption="Enrolled and active" href="/endpoints/devices" />);
    expect(screen.getByText("1,284")).toBeInTheDocument();
    expect(screen.getByRole("link", { name: /managed devices: 1,284/i })).toHaveAttribute("href", "/endpoints/devices");
  });

  it("never shows a number when the source is unavailable or failed", () => {
    const { rerender } = render(<MetricCard label="IT assets" value={99} icon={Monitor} state="unavailable" message="Asset inventory not connected" href="/x" />);
    expect(screen.getByText("Asset inventory not connected")).toBeInTheDocument();
    expect(screen.queryByText("99")).not.toBeInTheDocument();
    expect(screen.queryByRole("link")).not.toBeInTheDocument();
    rerender(<MetricCard label="IT assets" value={99} icon={Monitor} state="error" />);
    expect(screen.getByRole("alert")).toHaveTextContent("Couldn't load");
    expect(screen.queryByText("99")).not.toBeInTheDocument();
  });
});

describe("Pagination", () => {
  it("computes the visible row range", () => {
    expect(pageRange(1, 25, 312)).toEqual({ from: 1, to: 25 });
    expect(pageRange(13, 25, 312)).toEqual({ from: 301, to: 312 });
    expect(pageRange(1, 25, 0)).toEqual({ from: 0, to: 0 });
  });

  it("disables controls at the ends and reports page changes", async () => {
    const user = userEvent.setup();
    const onPageChange = vi.fn();
    render(<Pagination page={1} pageSize={25} totalCount={60} onPageChange={onPageChange} itemLabel="devices" />);
    expect(screen.getByText("1–25 of 60 devices")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Previous page" })).toBeDisabled();
    await user.click(screen.getByRole("button", { name: "Next page" }));
    expect(onPageChange).toHaveBeenCalledWith(2);
    await user.click(screen.getByRole("button", { name: "Last page" }));
    expect(onPageChange).toHaveBeenCalledWith(3);
  });
});

interface Row {
  id: string;
  name: string;
  os: string;
}

const rows: Row[] = [
  { id: "1", name: "ALPHA", os: "Windows 11" },
  { id: "2", name: "BRAVO", os: "Windows 10" },
];

const columns: ColumnDef<Row, unknown>[] = [
  { id: "name", accessorFn: (r) => r.name, enableHiding: false, meta: { label: "Device" }, header: ({ column }) => <DataTableColumnHeader column={column} title="Device" />, cell: ({ row }) => row.original.name },
  { id: "os", enableSorting: false, meta: { label: "Operating system" }, header: "Operating system", cell: ({ row }) => row.original.os },
];

function Harness({ data = rows, onSort, error, loading }: { data?: Row[]; onSort?: (s: SortingState) => void; error?: string; loading?: boolean }) {
  const [selection, setSelection] = React.useState<RowSelectionState>({});
  const [visibility, setVisibility] = React.useState<VisibilityState>({});
  const [sorting, setSorting] = React.useState<SortingState>([{ id: "name", desc: false }]);
  return (
    <DataTable
      label="Devices"
      columns={columns}
      data={data}
      getRowId={(r) => r.id}
      loading={loading}
      error={error}
      onRetry={() => {}}
      empty={<EmptyState title="No devices enrolled yet" />}
      sorting={sorting}
      onSortingChange={(updater) => {
        const next = typeof updater === "function" ? updater(sorting) : updater;
        setSorting(next);
        onSort?.(next);
      }}
      enableSelection
      rowSelection={selection}
      onRowSelectionChange={setSelection}
      columnVisibility={visibility}
      onColumnVisibilityChange={setVisibility}
      toolbar={(table) => <DataTableViewOptions table={table} />}
      bulkActions={(ids) => <button type="button">Retire {ids.length}</button>}
    />
  );
}

describe("DataTable", () => {
  it("selects rows individually and in bulk, and swaps the toolbar for bulk actions", async () => {
    const user = userEvent.setup();
    render(<Harness />);
    const selectAll = screen.getByRole("checkbox", { name: /select all rows/i });
    const rowBoxes = screen.getAllByRole("checkbox", { name: "Select row" });

    await user.click(rowBoxes[0]);
    expect(screen.getByText("1 selected")).toBeInTheDocument();
    expect(selectAll).toHaveAttribute("aria-checked", "mixed");
    expect(screen.queryByRole("button", { name: /columns/i })).not.toBeInTheDocument();

    await user.click(selectAll);
    expect(screen.getByRole("button", { name: "Retire 2" })).toBeInTheDocument();

    await user.click(screen.getByRole("button", { name: "Clear" }));
    expect(screen.queryByText(/selected/)).not.toBeInTheDocument();
    expect(screen.getByRole("button", { name: /columns/i })).toBeInTheDocument();
  });

  it("exposes and toggles sorting on sortable columns only", async () => {
    const user = userEvent.setup();
    const onSort = vi.fn();
    render(<Harness onSort={onSort} />);
    const header = screen.getByRole("columnheader", { name: /device/i });
    expect(header).toHaveAttribute("aria-sort", "ascending");
    expect(screen.getByRole("columnheader", { name: "Operating system" })).not.toHaveAttribute("aria-sort");
    await user.click(within(header).getByRole("button"));
    expect(onSort).toHaveBeenCalledWith([{ id: "name", desc: true }]);
    expect(header).toHaveAttribute("aria-sort", "descending");
  });

  it("hides optional columns from the Columns menu", async () => {
    const user = userEvent.setup();
    render(<Harness />);
    expect(screen.getByRole("columnheader", { name: "Operating system" })).toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: /columns/i }));
    // The identifying column cannot be hidden.
    expect(screen.queryByRole("menuitemcheckbox", { name: "Device" })).not.toBeInTheDocument();
    await user.click(screen.getByRole("menuitemcheckbox", { name: "Operating system" }));
    expect(screen.queryByRole("columnheader", { name: "Operating system" })).not.toBeInTheDocument();
  });

  it("renders empty, loading and error states", () => {
    const { rerender } = render(<Harness data={[]} />);
    expect(screen.getByText("No devices enrolled yet")).toBeInTheDocument();
    rerender(<Harness data={[]} loading />);
    expect(screen.getByRole("table", { name: "Devices" })).toHaveAttribute("aria-busy", "true");
    expect(screen.queryByText("No devices enrolled yet")).not.toBeInTheDocument();
    rerender(<Harness data={[]} error="The device list could not be loaded." />);
    expect(screen.getByRole("alert")).toHaveTextContent("The device list could not be loaded.");
    expect(screen.getByRole("button", { name: /try again/i })).toBeInTheDocument();
  });
});

describe("FacetedFilter", () => {
  it("selects one option and clears it when chosen again", async () => {
    const user = userEvent.setup();
    const onChange = vi.fn();
    const options = [
      { value: "Online", label: "Online", count: 4 },
      { value: "Offline", label: "Offline", count: 1 },
    ];
    const { rerender } = render(<FacetedFilter title="Status" options={options} value={[]} onChange={onChange} />);
    await user.click(screen.getByRole("button", { name: /status/i }));
    await user.click(screen.getByRole("option", { name: /online/i }));
    expect(onChange).toHaveBeenLastCalledWith(["Online"]);

    rerender(<FacetedFilter title="Status" options={options} value={["Online"]} onChange={onChange} />);
    await user.click(screen.getByRole("button", { name: /status/i }));
    expect(screen.getByRole("option", { name: /online/i })).toHaveAttribute("aria-selected", "true");
    await user.click(screen.getByRole("option", { name: /online/i }));
    expect(onChange).toHaveBeenLastCalledWith([]);
  });
});

describe("ConfirmDialog", () => {
  it("requires the typed phrase, runs the action and closes", async () => {
    const user = userEvent.setup();
    const onConfirm = vi.fn().mockResolvedValue(undefined);
    const onOpenChange = vi.fn();
    render(<ConfirmDialog open onOpenChange={onOpenChange} title="Delete 3 devices?" description="This cannot be undone." confirmLabel="Delete 3 devices" confirmText="delete" onConfirm={onConfirm} />);
    const dialog = screen.getByRole("alertdialog", { name: "Delete 3 devices?" });
    const confirm = within(dialog).getByRole("button", { name: "Delete 3 devices" });
    expect(confirm).toBeDisabled();
    await user.type(within(dialog).getByRole("textbox"), "delete");
    expect(confirm).toBeEnabled();
    await user.click(confirm);
    expect(onConfirm).toHaveBeenCalledTimes(1);
    expect(onOpenChange).toHaveBeenCalledWith(false);
  });

  it("stays open when the action fails", async () => {
    const user = userEvent.setup();
    const onOpenChange = vi.fn();
    render(<ConfirmDialog open onOpenChange={onOpenChange} title="Retire device?" description="Credentials are revoked." confirmLabel="Retire device" onConfirm={() => Promise.reject(new Error("nope"))} />);
    await user.click(screen.getByRole("button", { name: "Retire device" }));
    expect(onOpenChange).not.toHaveBeenCalledWith(false);
    expect(screen.getByRole("alertdialog")).toBeInTheDocument();
  });
});

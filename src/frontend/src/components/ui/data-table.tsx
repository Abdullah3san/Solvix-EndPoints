"use client";

import * as React from "react";
import {
  flexRender,
  getCoreRowModel,
  useReactTable,
  type Column,
  type ColumnDef,
  type OnChangeFn,
  type RowData,
  type RowSelectionState,
  type SortingState,
  type Table as TableInstance,
  type VisibilityState,
} from "@tanstack/react-table";
import { ArrowDown, ArrowUp, Check, ChevronsUpDown, ListFilter, SlidersHorizontal, X } from "lucide-react";
import { cn } from "@/lib/utils";
import { Button } from "./button";
import { Checkbox } from "./input";
import { DropdownMenu, DropdownMenuCheckboxItem, DropdownMenuContent, DropdownMenuItem, DropdownMenuLabel, DropdownMenuSeparator, DropdownMenuTrigger } from "./dropdown-menu";
import { ErrorState } from "./empty-state";
import { Pagination, type PaginationProps } from "./pagination";
import { Popover, PopoverContent, PopoverTrigger } from "./popover";
import { Skeleton } from "./skeleton";
import { Table, TBody, TD, TH, THead, TR } from "./table";

declare module "@tanstack/react-table" {
  // eslint-disable-next-line @typescript-eslint/no-unused-vars
  interface ColumnMeta<TData extends RowData, TValue> {
    /** Human-readable name, used by the column picker and for accessibility. */
    label?: string;
    align?: "left" | "right";
    /** Applied to both the header and body cells (e.g. a fixed width). */
    className?: string;
  }
}

export type { ColumnDef, RowSelectionState, SortingState, VisibilityState };

export interface DataTableProps<T> {
  columns: ColumnDef<T, unknown>[];
  data: T[];
  getRowId: (row: T) => string;
  /** Accessible name of the table, e.g. "Devices". */
  label: string;

  /** A request is in flight. With no rows yet, skeleton rows are shown. */
  loading?: boolean;
  /** Loading failed; replaces the body with an error state. */
  error?: string | null;
  onRetry?: () => void;
  /** Rendered when there are no rows and no error. */
  empty: React.ReactNode;

  sorting?: SortingState;
  onSortingChange?: OnChangeFn<SortingState>;

  enableSelection?: boolean;
  rowSelection?: RowSelectionState;
  onRowSelectionChange?: OnChangeFn<RowSelectionState>;
  /** Actions for the selected rows; rendered in a bar that replaces the toolbar. */
  bulkActions?: (selectedIds: string[], clear: () => void) => React.ReactNode;

  columnVisibility?: VisibilityState;
  onColumnVisibilityChange?: OnChangeFn<VisibilityState>;

  /** Search, filters and view options, rendered above the table. */
  toolbar?: (table: TableInstance<T>) => React.ReactNode;
  pagination?: PaginationProps;
  skeletonRows?: number;
  className?: string;
}

/**
 * Data table for server-driven lists: sorting, pagination and filtering are
 * controlled by the caller (the API does the work); selection and column
 * visibility are handled here. Renders loading, error and empty states.
 *
 * A column is sortable when it has an `accessorFn`/`accessorKey` and does not
 * set `enableSorting: false`. Only mark columns the API can actually sort.
 */
export function DataTable<T>({
  columns,
  data,
  getRowId,
  label,
  loading = false,
  error = null,
  onRetry,
  empty,
  sorting,
  onSortingChange,
  enableSelection = false,
  rowSelection,
  onRowSelectionChange,
  bulkActions,
  columnVisibility,
  onColumnVisibilityChange,
  toolbar,
  pagination,
  skeletonRows = 8,
  className,
}: DataTableProps<T>) {
  const allColumns = React.useMemo<ColumnDef<T, unknown>[]>(() => {
    if (!enableSelection) return columns;
    const select: ColumnDef<T, unknown> = {
      id: "select",
      enableSorting: false,
      enableHiding: false,
      meta: { className: "w-10 pr-0" },
      header: ({ table }) => (
        <Checkbox
          aria-label="Select all rows on this page"
          checked={table.getIsAllPageRowsSelected()}
          indeterminate={table.getIsSomePageRowsSelected() && !table.getIsAllPageRowsSelected()}
          onChange={(event) => table.toggleAllPageRowsSelected(event.target.checked)}
        />
      ),
      cell: ({ row }) => (
        <Checkbox aria-label="Select row" checked={row.getIsSelected()} disabled={!row.getCanSelect()} onChange={(event) => row.toggleSelected(event.target.checked)} />
      ),
    };
    return [select, ...columns];
  }, [columns, enableSelection]);

  // TanStack Table returns non-memoisable functions by design; this component is not compiled by React Compiler.
  // eslint-disable-next-line react-hooks/incompatible-library
  const table = useReactTable({
    data,
    columns: allColumns,
    getRowId,
    getCoreRowModel: getCoreRowModel(),
    manualSorting: true,
    manualPagination: true,
    manualFiltering: true,
    enableSortingRemoval: false,
    enableRowSelection: enableSelection,
    state: {
      sorting: sorting ?? [],
      rowSelection: rowSelection ?? {},
      columnVisibility: columnVisibility ?? {},
    },
    onSortingChange,
    onRowSelectionChange,
    onColumnVisibilityChange,
  });

  const selectedIds = Object.keys(rowSelection ?? {}).filter((id) => rowSelection?.[id]);
  const clearSelection = React.useCallback(() => onRowSelectionChange?.({}), [onRowSelectionChange]);
  const visibleColumnCount = table.getVisibleLeafColumns().length;
  const rows = table.getRowModel().rows;
  const initialLoading = loading && data.length === 0 && !error;

  return (
    <div className={cn("overflow-hidden rounded-lg border border-border bg-card shadow-xs", className)}>
      {selectedIds.length > 0 && bulkActions ? (
        <div role="region" aria-label="Bulk actions" className="flex min-h-[3.25rem] flex-wrap items-center gap-2 border-b border-border bg-accent/60 px-3 py-2">
          <span className="text-sm font-medium tabular-nums" aria-live="polite">
            {selectedIds.length} selected
          </span>
          <Button variant="ghost" size="sm" onClick={clearSelection}>
            Clear
          </Button>
          <span className="mx-1 hidden h-5 w-px bg-border sm:block" aria-hidden />
          <div className="flex flex-wrap items-center gap-2">{bulkActions(selectedIds, clearSelection)}</div>
        </div>
      ) : toolbar ? (
        <div className="flex min-h-[3.25rem] flex-wrap items-center gap-2 border-b border-border px-3 py-2">{toolbar(table)}</div>
      ) : null}

      <Table aria-label={label} aria-busy={loading || undefined}>
        <THead>
          {table.getHeaderGroups().map((group) => (
            <TR key={group.id}>
              {group.headers.map((header) => {
                const sorted = header.column.getIsSorted();
                return (
                  <TH
                    key={header.id}
                    scope="col"
                    aria-sort={header.column.getCanSort() ? (sorted === "asc" ? "ascending" : sorted === "desc" ? "descending" : "none") : undefined}
                    className={cn(header.column.columnDef.meta?.align === "right" && "text-right", header.column.columnDef.meta?.className)}
                  >
                    {header.isPlaceholder ? null : flexRender(header.column.columnDef.header, header.getContext())}
                  </TH>
                );
              })}
            </TR>
          ))}
        </THead>
        <TBody className={cn(loading && data.length > 0 && "opacity-60 transition-opacity")}>
          {error ? (
            <TR className="hover:bg-transparent">
              <TD colSpan={visibleColumnCount} className="p-0">
                <ErrorState description={error} onRetry={onRetry} retrying={loading} />
              </TD>
            </TR>
          ) : initialLoading ? (
            Array.from({ length: skeletonRows }, (_, index) => (
              <TR key={index} className="hover:bg-transparent">
                {table.getVisibleLeafColumns().map((column) => (
                  <TD key={column.id} className={column.columnDef.meta?.className}>
                    <Skeleton className={cn("h-4", column.id === "select" ? "w-4" : index % 2 ? "w-3/5" : "w-4/5")} />
                  </TD>
                ))}
              </TR>
            ))
          ) : rows.length === 0 ? (
            <TR className="hover:bg-transparent">
              <TD colSpan={visibleColumnCount} className="p-0">
                {empty}
              </TD>
            </TR>
          ) : (
            rows.map((row) => (
              <TR key={row.id} data-state={row.getIsSelected() ? "selected" : undefined}>
                {row.getVisibleCells().map((cell) => (
                  <TD key={cell.id} className={cn(cell.column.columnDef.meta?.align === "right" && "text-right", cell.column.columnDef.meta?.className)}>
                    {flexRender(cell.column.columnDef.cell, cell.getContext())}
                  </TD>
                ))}
              </TR>
            ))
          )}
        </TBody>
      </Table>

      {pagination && !error ? <Pagination {...pagination} disabled={loading || pagination.disabled} className="border-t border-border" /> : null}
    </div>
  );
}

/** Column header: a sort button when the column is sortable, plain text otherwise. */
export function DataTableColumnHeader<T, V>({ column, title, className }: { column: Column<T, V>; title: string; className?: string }) {
  if (!column.getCanSort()) return <span className={className}>{title}</span>;
  const sorted = column.getIsSorted();
  const Icon = sorted === "asc" ? ArrowUp : sorted === "desc" ? ArrowDown : ChevronsUpDown;
  return (
    <button
      type="button"
      onClick={() => column.toggleSorting(sorted === "asc")}
      className={cn("-ml-1.5 inline-flex h-7 items-center gap-1 rounded px-1.5 font-medium hover:bg-muted hover:text-foreground", sorted && "text-foreground", className)}
      title={sorted === "asc" ? "Sorted ascending. Click to sort descending." : sorted === "desc" ? "Sorted descending. Click to sort ascending." : "Click to sort ascending."}
    >
      {title}
      <Icon className={cn("size-3.5", !sorted && "opacity-50")} aria-hidden />
    </button>
  );
}

/** "Columns" menu: show or hide optional columns. */
export function DataTableViewOptions<T>({ table, onReset }: { table: TableInstance<T>; onReset?: () => void }) {
  const hideable = table.getAllLeafColumns().filter((column) => column.getCanHide());
  return (
    <DropdownMenu>
      <DropdownMenuTrigger asChild>
        <Button variant="outline" size="sm">
          <SlidersHorizontal /> Columns
        </Button>
      </DropdownMenuTrigger>
      <DropdownMenuContent className="w-52">
        <DropdownMenuLabel>Show columns</DropdownMenuLabel>
        {hideable.map((column) => (
          <DropdownMenuCheckboxItem
            key={column.id}
            checked={column.getIsVisible()}
            onCheckedChange={(checked) => column.toggleVisibility(checked === true)}
            onSelect={(event) => event.preventDefault()}
          >
            {column.columnDef.meta?.label ?? column.id}
          </DropdownMenuCheckboxItem>
        ))}
        {onReset ? (
          <>
            <DropdownMenuSeparator />
            <DropdownMenuItem onSelect={onReset}>Reset to default</DropdownMenuItem>
          </>
        ) : null}
      </DropdownMenuContent>
    </DropdownMenu>
  );
}

export interface FilterOption {
  value: string;
  label: string;
  /** Optional count shown next to the option. Only pass real counts. */
  count?: number;
}

/**
 * Filter chip that opens a list of options. Single-select by default (choosing
 * the active option again clears it); `multiple` allows several values.
 */
export function FacetedFilter({
  title,
  options,
  value,
  onChange,
  multiple = false,
  emptyText = "No options",
}: {
  title: string;
  options: FilterOption[];
  value: string[];
  onChange: (value: string[]) => void;
  multiple?: boolean;
  emptyText?: string;
}) {
  const [open, setOpen] = React.useState(false);
  const selected = options.filter((option) => value.includes(option.value));

  function toggle(option: string) {
    if (multiple) {
      onChange(value.includes(option) ? value.filter((v) => v !== option) : [...value, option]);
    } else {
      onChange(value.includes(option) ? [] : [option]);
      setOpen(false);
    }
  }

  return (
    <Popover open={open} onOpenChange={setOpen}>
      <PopoverTrigger asChild>
        <Button variant="outline" size="sm" className={cn("border-dashed", selected.length > 0 && "border-solid border-primary/40 bg-accent/60")}>
          <ListFilter /> {title}
          {selected.length > 0 ? (
            <>
              <span className="h-4 w-px bg-border" aria-hidden />
              <span className="max-w-32 truncate font-normal">{selected.length === 1 ? selected[0].label : `${selected.length} selected`}</span>
            </>
          ) : null}
        </Button>
      </PopoverTrigger>
      <PopoverContent className="w-60 p-1">
        <p className="px-2.5 py-1.5 text-xs font-medium text-muted-foreground">Filter by {title.toLowerCase()}</p>
        {options.length === 0 ? (
          <p className="px-2.5 py-4 text-center text-sm text-muted-foreground">{emptyText}</p>
        ) : (
          <div role="listbox" aria-label={title} aria-multiselectable={multiple || undefined} className="scrollbar-thin max-h-64 overflow-y-auto">
            {options.map((option) => {
              const active = value.includes(option.value);
              return (
                <button
                  key={option.value}
                  type="button"
                  role="option"
                  aria-selected={active}
                  onClick={() => toggle(option.value)}
                  className="flex w-full items-center gap-2 rounded-md px-2.5 py-1.5 text-left text-sm hover:bg-muted"
                >
                  <span className={cn("flex size-4 shrink-0 items-center justify-center border border-input", multiple ? "rounded" : "rounded-full", active && "border-primary bg-primary text-primary-foreground")}>
                    {active ? <Check className="size-3" aria-hidden /> : null}
                  </span>
                  <span className="min-w-0 flex-1 truncate">{option.label}</span>
                  {option.count !== undefined ? <span className="text-xs tabular-nums text-muted-foreground">{option.count}</span> : null}
                </button>
              );
            })}
          </div>
        )}
        {selected.length > 0 ? (
          <div className="mt-1 border-t border-border pt-1">
            <button
              type="button"
              onClick={() => {
                onChange([]);
                setOpen(false);
              }}
              className="w-full rounded-md px-2.5 py-1.5 text-center text-sm hover:bg-muted"
            >
              Clear filter
            </button>
          </div>
        ) : null}
      </PopoverContent>
    </Popover>
  );
}

/** "Reset" control shown in a toolbar while any filter is active. */
export function ResetFiltersButton({ onClick }: { onClick: () => void }) {
  return (
    <Button variant="ghost" size="sm" onClick={onClick}>
      Reset <X />
    </Button>
  );
}

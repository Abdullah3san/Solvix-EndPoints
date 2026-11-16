"use client";

import { ChevronLeft, ChevronRight, ChevronsLeft, ChevronsRight } from "lucide-react";
import { cn } from "@/lib/utils";
import { Button } from "./button";
import { Select } from "./input";

/** "26–50 of 312" — the row range a page shows. */
export function pageRange(page: number, pageSize: number, totalCount: number): { from: number; to: number } {
  if (totalCount <= 0) return { from: 0, to: 0 };
  const from = (page - 1) * pageSize + 1;
  return { from: Math.min(from, totalCount), to: Math.min(page * pageSize, totalCount) };
}

export interface PaginationProps {
  page: number;
  pageSize: number;
  totalCount: number;
  onPageChange: (page: number) => void;
  onPageSizeChange?: (pageSize: number) => void;
  pageSizeOptions?: number[];
  /** Plural noun for the count, e.g. "devices". */
  itemLabel?: string;
  disabled?: boolean;
  className?: string;
}

/** Table footer: row range, rows-per-page and first/previous/next/last controls. */
export function Pagination({
  page,
  pageSize,
  totalCount,
  onPageChange,
  onPageSizeChange,
  pageSizeOptions = [10, 25, 50, 100],
  itemLabel = "rows",
  disabled = false,
  className,
}: PaginationProps) {
  const totalPages = Math.max(1, Math.ceil(totalCount / pageSize));
  const { from, to } = pageRange(page, pageSize, totalCount);
  const number = new Intl.NumberFormat();

  return (
    <nav aria-label="Pagination" className={cn("flex flex-col gap-3 px-3 py-2.5 text-sm text-muted-foreground sm:flex-row sm:items-center sm:justify-between", className)}>
      <p className="tabular-nums" aria-live="polite">
        {totalCount === 0 ? `No ${itemLabel}` : `${number.format(from)}–${number.format(to)} of ${number.format(totalCount)} ${itemLabel}`}
      </p>
      <div className="flex items-center gap-4">
        {onPageSizeChange ? (
          <label className="flex items-center gap-2 whitespace-nowrap">
            Rows per page
            <Select value={pageSize} onChange={(event) => onPageSizeChange(Number(event.target.value))} disabled={disabled} className="h-8 w-[4.5rem] px-2">
              {pageSizeOptions.map((option) => (
                <option key={option} value={option}>
                  {option}
                </option>
              ))}
            </Select>
          </label>
        ) : null}
        <div className="flex items-center gap-1">
          <span className="mr-1 hidden whitespace-nowrap tabular-nums sm:inline">
            Page {number.format(page)} of {number.format(totalPages)}
          </span>
          <Button variant="outline" size="icon-sm" disabled={disabled || page <= 1} onClick={() => onPageChange(1)} aria-label="First page" className="hidden sm:inline-flex">
            <ChevronsLeft />
          </Button>
          <Button variant="outline" size="icon-sm" disabled={disabled || page <= 1} onClick={() => onPageChange(page - 1)} aria-label="Previous page">
            <ChevronLeft />
          </Button>
          <Button variant="outline" size="icon-sm" disabled={disabled || page >= totalPages} onClick={() => onPageChange(page + 1)} aria-label="Next page">
            <ChevronRight />
          </Button>
          <Button variant="outline" size="icon-sm" disabled={disabled || page >= totalPages} onClick={() => onPageChange(totalPages)} aria-label="Last page" className="hidden sm:inline-flex">
            <ChevronsRight />
          </Button>
        </div>
      </div>
    </nav>
  );
}

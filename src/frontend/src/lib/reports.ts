import type { ReportColumnDto } from "@/lib/api/types";

/** Renders a report cell by column type (dates are shown in the viewer's locale, numbers with grouping). */
export function formatCell(value: string | number | boolean | null | undefined, column: Pick<ReportColumnDto, "type">): string {
  if (value === null || value === undefined || value === "") return "—";
  switch (column.type) {
    case "bool":
      return value === true || value === "true" ? "Yes" : "No";
    case "number":
      return typeof value === "number" ? value.toLocaleString("en-US", { maximumFractionDigits: 2 }) : String(value);
    case "datetime": {
      const d = new Date(String(value));
      return Number.isNaN(d.getTime()) ? String(value) : d.toISOString().replace("T", " ").slice(0, 16) + " UTC";
    }
    default:
      return String(value);
  }
}

export function formatMetric(value: string | number | null): string {
  if (value === null || value === undefined) return "—";
  return typeof value === "number" ? value.toLocaleString("en-US", { maximumFractionDigits: 2 }) : value;
}

/** Report filters the UI knows how to render. Anything else the API lists is ignored rather than guessed. */
export const supportedFilters = ["search", "days", "branchId"] as const;

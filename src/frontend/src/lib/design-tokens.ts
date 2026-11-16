/**
 * TypeScript mirror of the design tokens declared in app/globals.css.
 *
 * CSS custom properties stay the single source of truth for values; this file
 * only exposes the token *names* so charts, docs and tests can reference them
 * without string typos. See docs/DESIGN-SYSTEM.md.
 */
export const colorTokens = {
  surface: ["background", "card", "popover", "muted"],
  text: ["foreground", "muted-foreground", "link"],
  action: ["primary", "secondary", "accent", "highlight"],
  status: ["success", "warning", "destructive", "info"],
  line: ["border", "input", "ring"],
  sidebar: ["sidebar", "sidebar-active", "sidebar-indicator", "sidebar-muted"],
} as const;

/** Brand ramps (primitives). Components never use these directly. */
export const brandRamps = {
  navy: [50, 100, 200, 300, 400, 500, 600, 700, 800, 900, 950],
  blue: [50, 100, 300, 400, 500, 600, 700],
  cyan: [100, 300, 400, 500, 600, 700],
  slate: [0, 50, 100, 200, 300, 400, 500, 600, 700, 800, 900, 950],
} as const;

/**
 * Categorical chart colours in their fixed order. Assign by entity, never by
 * rank, and never cycle past the fifth slot: fold the tail into "Other".
 */
export const chartColors = ["var(--chart-1)", "var(--chart-2)", "var(--chart-3)", "var(--chart-4)", "var(--chart-5)"] as const;

export const chartChrome = {
  neutral: "var(--chart-neutral)",
  grid: "var(--chart-grid)",
  axis: "var(--chart-axis)",
  text: "var(--muted-foreground)",
} as const;

/** Status tones shared by badges, alerts, toasts and metric cards. */
export type Tone = "neutral" | "brand" | "info" | "success" | "warning" | "danger";

export const radii = ["sm", "md", "lg", "xl"] as const;
export const elevations = ["shadow-xs", "shadow-md", "shadow-xl"] as const;

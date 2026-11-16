import { cn } from "@/lib/utils";

/** Placeholder block shown while content loads. Size it like the content it replaces. */
export function Skeleton({ className }: { className?: string }) {
  return <div className={cn("animate-pulse rounded-md bg-muted", className)} aria-hidden />;
}

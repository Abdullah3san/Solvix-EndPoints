import * as React from "react";
import { Inbox, RotateCw, TriangleAlert, type LucideIcon } from "lucide-react";
import { cn } from "@/lib/utils";
import { Button } from "./button";

interface EmptyStateProps {
  icon?: LucideIcon;
  title: string;
  description?: React.ReactNode;
  /** Primary next step, e.g. a "Create" button or a link. */
  action?: React.ReactNode;
  size?: "sm" | "md";
  className?: string;
}

/** Shown when a view has nothing to display. Always says what to do next. */
export function EmptyState({ icon: Icon = Inbox, title, description, action, size = "md", className }: EmptyStateProps) {
  return (
    <div className={cn("flex flex-col items-center justify-center text-center", size === "md" ? "px-6 py-12" : "px-4 py-8", className)}>
      <span className={cn("flex items-center justify-center rounded-full bg-muted text-muted-foreground", size === "md" ? "size-11" : "size-9")}>
        <Icon className={size === "md" ? "size-5" : "size-4"} aria-hidden />
      </span>
      <p className="mt-3 text-sm font-medium">{title}</p>
      {description ? <p className="mt-1 max-w-sm text-sm text-muted-foreground">{description}</p> : null}
      {action ? <div className="mt-4 flex items-center gap-2">{action}</div> : null}
    </div>
  );
}

/** Shown when loading a view failed. Offers a retry when one is possible. */
export function ErrorState({
  title = "Couldn't load this data",
  description,
  onRetry,
  retrying = false,
  size = "md",
  className,
}: {
  title?: string;
  description?: React.ReactNode;
  onRetry?: () => void;
  retrying?: boolean;
  size?: "sm" | "md";
  className?: string;
}) {
  return (
    <div role="alert" className={cn("flex flex-col items-center justify-center text-center", size === "md" ? "px-6 py-12" : "px-4 py-8", className)}>
      <span className={cn("flex items-center justify-center rounded-full bg-destructive/10 text-destructive", size === "md" ? "size-11" : "size-9")}>
        <TriangleAlert className={size === "md" ? "size-5" : "size-4"} aria-hidden />
      </span>
      <p className="mt-3 text-sm font-medium">{title}</p>
      {description ? <p className="mt-1 max-w-sm text-sm text-muted-foreground">{description}</p> : null}
      {onRetry ? (
        <Button variant="outline" size="sm" className="mt-4" onClick={onRetry} loading={retrying}>
          {retrying ? null : <RotateCw />} Try again
        </Button>
      ) : null}
    </div>
  );
}

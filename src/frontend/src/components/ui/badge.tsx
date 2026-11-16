import * as React from "react";
import { cva } from "class-variance-authority";
import { cn } from "@/lib/utils";
import type { Tone } from "@/lib/design-tokens";

export const badgeVariants = cva("inline-flex items-center gap-1.5 whitespace-nowrap rounded-full px-2 py-0.5 text-xs font-medium", {
  variants: {
    tone: {
      neutral: "bg-muted text-muted-foreground",
      brand: "bg-secondary text-secondary-foreground",
      info: "bg-info/12 text-info",
      success: "bg-success/12 text-success",
      warning: "bg-warning/14 text-warning",
      danger: "bg-destructive/12 text-destructive",
    } satisfies Record<Tone, string>,
  },
  defaultVariants: { tone: "neutral" },
});

export function Badge({ tone = "neutral", className, ...props }: React.HTMLAttributes<HTMLSpanElement> & { tone?: Tone }) {
  return <span className={cn(badgeVariants({ tone }), className)} {...props} />;
}

const dotTone: Record<Tone, string> = {
  neutral: "bg-muted-foreground/60",
  brand: "bg-primary",
  info: "bg-info",
  success: "bg-success",
  warning: "bg-warning",
  danger: "bg-destructive",
};

/**
 * Status badge: a tone plus a leading dot, so state is never carried by colour
 * alone. `pulse` marks a live state (e.g. a device that is online right now).
 */
export function StatusBadge({
  tone = "neutral",
  pulse = false,
  className,
  children,
  ...props
}: React.HTMLAttributes<HTMLSpanElement> & { tone?: Tone; pulse?: boolean }) {
  return (
    <Badge tone={tone} className={className} {...props}>
      <span className="relative flex size-1.5" aria-hidden>
        {pulse ? <span className={cn("absolute inline-flex size-full animate-ping rounded-full opacity-60", dotTone[tone])} /> : null}
        <span className={cn("relative inline-flex size-1.5 rounded-full", dotTone[tone])} />
      </span>
      {children}
    </Badge>
  );
}

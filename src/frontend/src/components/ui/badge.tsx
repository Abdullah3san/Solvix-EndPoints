import * as React from "react";
import { cn } from "@/lib/utils";

type Tone = "neutral" | "brand" | "success" | "warning" | "danger";

const tones: Record<Tone, string> = {
  neutral: "bg-muted text-muted-foreground",
  brand: "bg-secondary text-secondary-foreground",
  success: "bg-success/15 text-success",
  warning: "bg-warning/15 text-warning",
  danger: "bg-destructive/15 text-destructive",
};

export function Badge({ tone = "neutral", className, ...props }: React.HTMLAttributes<HTMLSpanElement> & { tone?: Tone }) {
  return (
    <span
      className={cn("inline-flex items-center rounded-full px-2 py-0.5 text-xs font-medium", tones[tone], className)}
      {...props}
    />
  );
}

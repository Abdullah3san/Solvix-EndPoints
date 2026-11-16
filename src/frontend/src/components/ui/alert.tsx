import * as React from "react";
import { AlertTriangle, CheckCircle2, Info, OctagonAlert } from "lucide-react";
import { cn } from "@/lib/utils";

type Tone = "info" | "success" | "warning" | "error";

const styles: Record<Tone, string> = {
  info: "border-info/25 bg-info/8",
  success: "border-success/30 bg-success/8",
  warning: "border-warning/35 bg-warning/10",
  error: "border-destructive/30 bg-destructive/8",
};

const iconStyles: Record<Tone, string> = {
  info: "text-info",
  success: "text-success",
  warning: "text-warning",
  error: "text-destructive",
};

const icons: Record<Tone, React.ComponentType<{ className?: string }>> = {
  info: Info,
  success: CheckCircle2,
  warning: AlertTriangle,
  error: OctagonAlert,
};

/** Inline message that stays on the page (use a toast for transient feedback). */
export function Alert({
  tone = "info",
  title,
  children,
  action,
  className,
}: {
  tone?: Tone;
  title?: string;
  children?: React.ReactNode;
  action?: React.ReactNode;
  className?: string;
}) {
  const Icon = icons[tone];
  return (
    <div role={tone === "error" ? "alert" : "status"} className={cn("flex gap-3 rounded-md border p-3 text-sm text-foreground", styles[tone], className)}>
      <Icon className={cn("mt-0.5 size-4 shrink-0", iconStyles[tone])} />
      <div className="min-w-0 flex-1 space-y-0.5">
        {title ? <p className="font-medium">{title}</p> : null}
        {children ? <div className="text-muted-foreground">{children}</div> : null}
      </div>
      {action ? <div className="shrink-0">{action}</div> : null}
    </div>
  );
}

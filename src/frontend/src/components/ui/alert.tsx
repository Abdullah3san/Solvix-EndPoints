import * as React from "react";
import { AlertTriangle, CheckCircle2, Info } from "lucide-react";
import { cn } from "@/lib/utils";

type Tone = "info" | "success" | "error";

const styles: Record<Tone, string> = {
  info: "border-border bg-muted text-foreground",
  success: "border-success/30 bg-success/10 text-foreground",
  error: "border-destructive/30 bg-destructive/10 text-foreground",
};

const icons: Record<Tone, React.ComponentType<{ className?: string }>> = {
  info: Info,
  success: CheckCircle2,
  error: AlertTriangle,
};

export function Alert({ tone = "info", title, children, className }: { tone?: Tone; title?: string; children?: React.ReactNode; className?: string }) {
  const Icon = icons[tone];
  return (
    <div role={tone === "error" ? "alert" : "status"} className={cn("flex gap-3 rounded-md border p-3 text-sm", styles[tone], className)}>
      <Icon className="mt-0.5 h-4 w-4 shrink-0" />
      <div className="space-y-0.5">
        {title ? <p className="font-medium">{title}</p> : null}
        {children ? <div className="text-muted-foreground">{children}</div> : null}
      </div>
    </div>
  );
}

"use client";

import * as React from "react";
import { CheckCircle2, AlertTriangle, Info, X } from "lucide-react";
import { cn } from "@/lib/utils";

type ToastTone = "success" | "error" | "info";
interface Toast {
  id: string;
  title: string;
  description?: string;
  tone: ToastTone;
}

const ToastContext = React.createContext<{ toast: (t: Omit<Toast, "id">) => void } | null>(null);

export function ToastProvider({ children }: { children: React.ReactNode }) {
  const [toasts, setToasts] = React.useState<Toast[]>([]);
  const remove = React.useCallback((id: string) => setToasts((c) => c.filter((t) => t.id !== id)), []);
  const toast = React.useCallback(
    (t: Omit<Toast, "id">) => {
      const id = crypto.randomUUID();
      setToasts((c) => [...c, { ...t, id }]);
      window.setTimeout(() => remove(id), t.tone === "error" ? 7000 : 4000);
    },
    [remove],
  );
  const value = React.useMemo(() => ({ toast }), [toast]);
  const icons: Record<ToastTone, React.ComponentType<{ className?: string }>> = { success: CheckCircle2, error: AlertTriangle, info: Info };

  return (
    <ToastContext.Provider value={value}>
      {children}
      <div className="pointer-events-none fixed bottom-4 right-4 z-[60] flex w-full max-w-sm flex-col gap-2" aria-live="polite">
        {toasts.map((t) => {
          const Icon = icons[t.tone];
          return (
            <div
              key={t.id}
              role="status"
              className={cn(
                "pointer-events-auto flex items-start gap-3 rounded-lg border bg-card p-3 text-sm shadow-lg",
                t.tone === "error" ? "border-destructive/40" : t.tone === "success" ? "border-success/40" : "border-border",
              )}
            >
              <Icon className={cn("mt-0.5 h-4 w-4 shrink-0", t.tone === "error" ? "text-destructive" : t.tone === "success" ? "text-success" : "text-muted-foreground")} />
              <div className="flex-1">
                <p className="font-medium">{t.title}</p>
                {t.description ? <p className="text-muted-foreground">{t.description}</p> : null}
              </div>
              <button type="button" onClick={() => remove(t.id)} className="rounded p-0.5 text-muted-foreground hover:text-foreground" aria-label="Dismiss">
                <X className="h-4 w-4" />
              </button>
            </div>
          );
        })}
      </div>
    </ToastContext.Provider>
  );
}

export function useToast() {
  const ctx = React.useContext(ToastContext);
  if (!ctx) throw new Error("useToast must be used inside <ToastProvider>");
  return ctx;
}

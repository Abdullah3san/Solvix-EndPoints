"use client";

import * as React from "react";
import { useTheme } from "next-themes";
import { Toaster, toast as sonner } from "sonner";
import { CircleCheck, Info, OctagonAlert, TriangleAlert } from "lucide-react";

export type ToastTone = "success" | "error" | "warning" | "info";

export interface ToastInput {
  title: string;
  description?: string;
  tone: ToastTone;
  /** Optional follow-up, e.g. "Undo" or "View". */
  action?: { label: string; onClick: () => void };
}

/** Transient feedback for the result of an action. Errors stay longer than successes. */
export function toast({ title, description, tone, action }: ToastInput) {
  const options = { description, action, duration: tone === "error" ? 8000 : 4500 };
  if (tone === "success") sonner.success(title, options);
  else if (tone === "error") sonner.error(title, options);
  else if (tone === "warning") sonner.warning(title, options);
  else sonner.info(title, options);
}

const api = { toast };

export function useToast() {
  return api;
}

/** Mounts the toast region once at the root; styling comes from the design tokens. */
export function ToastProvider({ children }: { children: React.ReactNode }) {
  const { resolvedTheme } = useTheme();
  return (
    <>
      {children}
      <Toaster
        theme={resolvedTheme === "dark" ? "dark" : "light"}
        position="bottom-right"
        closeButton
        visibleToasts={4}
        icons={{
          success: <CircleCheck className="size-4 text-success" />,
          error: <OctagonAlert className="size-4 text-destructive" />,
          warning: <TriangleAlert className="size-4 text-warning" />,
          info: <Info className="size-4 text-info" />,
        }}
        toastOptions={{
          classNames: {
            toast: "!rounded-lg !border !border-border !bg-popover !text-popover-foreground !shadow-md !font-sans",
            title: "!text-sm !font-medium",
            description: "!text-sm !text-muted-foreground",
            actionButton: "!bg-primary !text-primary-foreground !rounded-md",
            closeButton: "!bg-popover !border-border !text-muted-foreground",
          },
        }}
      />
    </>
  );
}

"use client";

import * as React from "react";
import { AlertDialog as AlertDialogPrimitive } from "radix-ui";
import { TriangleAlert } from "lucide-react";
import { cn } from "@/lib/utils";
import { Button } from "./button";
import { Input } from "./input";
import { overlayClasses } from "./dialog";

export interface ConfirmDialogProps {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  title: string;
  /** What will happen and whether it can be undone. */
  description: React.ReactNode;
  confirmLabel: string;
  cancelLabel?: string;
  /** `danger` (default) styles the action as destructive. */
  tone?: "danger" | "default";
  /** Runs the action; the dialog shows progress and closes when it resolves. A rejection keeps it open. */
  onConfirm: () => void | Promise<void>;
  /** For high-impact actions: the user must type this text to enable the action. */
  confirmText?: string;
  children?: React.ReactNode;
}

/**
 * Confirmation for destructive or irreversible actions. Unlike a plain dialog
 * it cannot be dismissed by clicking outside, and focus starts on Cancel.
 */
export function ConfirmDialog({
  open,
  onOpenChange,
  title,
  description,
  confirmLabel,
  cancelLabel = "Cancel",
  tone = "danger",
  onConfirm,
  confirmText,
  children,
}: ConfirmDialogProps) {
  const [pending, setPending] = React.useState(false);
  const [typed, setTyped] = React.useState("");
  const inputId = React.useId();
  const blocked = confirmText !== undefined && typed.trim() !== confirmText;

  async function confirm() {
    setPending(true);
    try {
      await onConfirm();
      onOpenChange(false);
    } catch {
      // The caller reports the failure (toast); keep the dialog open for a retry.
    } finally {
      setPending(false);
    }
  }

  return (
    <AlertDialogPrimitive.Root
      open={open}
      onOpenChange={(next) => {
        if (pending) return;
        if (!next) setTyped("");
        onOpenChange(next);
      }}
    >
      <AlertDialogPrimitive.Portal>
        <AlertDialogPrimitive.Overlay className={overlayClasses} />
        <AlertDialogPrimitive.Content
          className="fixed left-1/2 top-1/2 z-50 w-[calc(100vw-2rem)] max-w-md -translate-x-1/2 -translate-y-1/2 rounded-xl border border-border bg-popover p-6 text-popover-foreground shadow-xl duration-150 data-[state=open]:animate-in data-[state=closed]:animate-out data-[state=closed]:fade-out-0 data-[state=open]:fade-in-0 data-[state=closed]:zoom-out-95 data-[state=open]:zoom-in-95"
        >
          <div className="flex gap-4">
            {tone === "danger" ? (
              <span className="flex size-10 shrink-0 items-center justify-center rounded-full bg-destructive/10 text-destructive">
                <TriangleAlert className="size-5" aria-hidden />
              </span>
            ) : null}
            <div className="min-w-0 space-y-1.5">
              <AlertDialogPrimitive.Title className="text-base font-semibold leading-6">{title}</AlertDialogPrimitive.Title>
              <AlertDialogPrimitive.Description asChild>
                <div className="text-sm text-muted-foreground">{description}</div>
              </AlertDialogPrimitive.Description>
            </div>
          </div>
          {children ? <div className="mt-4">{children}</div> : null}
          {confirmText !== undefined ? (
            <div className="mt-4 space-y-1.5">
              <label htmlFor={inputId} className="text-sm">
                Type <span className="font-mono font-medium">{confirmText}</span> to confirm
              </label>
              <Input id={inputId} value={typed} onChange={(event) => setTyped(event.target.value)} autoComplete="off" spellCheck={false} />
            </div>
          ) : null}
          <div className={cn("mt-6 flex flex-col-reverse gap-2 sm:flex-row sm:justify-end")}>
            <AlertDialogPrimitive.Cancel asChild>
              <Button variant="outline" disabled={pending}>
                {cancelLabel}
              </Button>
            </AlertDialogPrimitive.Cancel>
            <Button variant={tone === "danger" ? "destructive" : "primary"} onClick={confirm} loading={pending} disabled={blocked}>
              {confirmLabel}
            </Button>
          </div>
        </AlertDialogPrimitive.Content>
      </AlertDialogPrimitive.Portal>
    </AlertDialogPrimitive.Root>
  );
}

"use client";

import * as React from "react";
import { Dialog as SheetPrimitive } from "radix-ui";
import { X } from "lucide-react";
import { cn } from "@/lib/utils";
import { overlayClasses } from "./dialog";

/** Drawer: a panel that slides in from an edge, for detail views and secondary tasks that keep the page in context. */
export const Sheet = SheetPrimitive.Root;
export const SheetTrigger = SheetPrimitive.Trigger;
export const SheetClose = SheetPrimitive.Close;

const sides = {
  right: "inset-y-0 right-0 h-full w-[calc(100vw-3rem)] border-l data-[state=closed]:slide-out-to-right data-[state=open]:slide-in-from-right",
  left: "inset-y-0 left-0 h-full w-[calc(100vw-3rem)] border-r data-[state=closed]:slide-out-to-left data-[state=open]:slide-in-from-left",
  bottom: "inset-x-0 bottom-0 max-h-[85dvh] rounded-t-xl border-t data-[state=closed]:slide-out-to-bottom data-[state=open]:slide-in-from-bottom",
} as const;

export const SheetContent = React.forwardRef<
  HTMLDivElement,
  React.ComponentPropsWithoutRef<typeof SheetPrimitive.Content> & { side?: keyof typeof sides; hideClose?: boolean }
>(function SheetContent({ side = "right", className, children, hideClose = false, ...props }, ref) {
  return (
    <SheetPrimitive.Portal>
      <SheetPrimitive.Overlay className={overlayClasses} />
      <SheetPrimitive.Content
        ref={ref}
        className={cn(
          "fixed z-50 flex flex-col border-border bg-popover text-popover-foreground shadow-xl duration-200 data-[state=open]:animate-in data-[state=closed]:animate-out",
          sides[side],
          side !== "bottom" && "sm:max-w-md",
          className,
        )}
        {...props}
      >
        {children}
        {hideClose ? null : (
          <SheetPrimitive.Close
            className="absolute right-3 top-3 flex size-8 items-center justify-center rounded-md text-muted-foreground hover:bg-muted hover:text-foreground"
            aria-label="Close panel"
          >
            <X className="size-4" />
          </SheetPrimitive.Close>
        )}
      </SheetPrimitive.Content>
    </SheetPrimitive.Portal>
  );
});

export function SheetHeader({ className, ...props }: React.HTMLAttributes<HTMLDivElement>) {
  return <div className={cn("space-y-1 border-b border-border px-5 py-4 pr-12", className)} {...props} />;
}

export function SheetBody({ className, ...props }: React.HTMLAttributes<HTMLDivElement>) {
  return <div className={cn("scrollbar-thin min-h-0 flex-1 overflow-y-auto px-5 py-4", className)} {...props} />;
}

export function SheetFooter({ className, ...props }: React.HTMLAttributes<HTMLDivElement>) {
  return <div className={cn("flex items-center justify-end gap-2 border-t border-border px-5 py-3", className)} {...props} />;
}

export const SheetTitle = React.forwardRef<HTMLHeadingElement, React.ComponentPropsWithoutRef<typeof SheetPrimitive.Title>>(
  function SheetTitle({ className, ...props }, ref) {
    return <SheetPrimitive.Title ref={ref} className={cn("text-base font-semibold leading-6", className)} {...props} />;
  },
);

export const SheetDescription = React.forwardRef<HTMLParagraphElement, React.ComponentPropsWithoutRef<typeof SheetPrimitive.Description>>(
  function SheetDescription({ className, ...props }, ref) {
    return <SheetPrimitive.Description ref={ref} className={cn("text-sm text-muted-foreground", className)} {...props} />;
  },
);

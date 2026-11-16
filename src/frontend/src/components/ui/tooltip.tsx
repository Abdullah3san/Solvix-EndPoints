"use client";

import * as React from "react";
import { Tooltip as TooltipPrimitive } from "radix-ui";
import { cn } from "@/lib/utils";

export const TooltipProvider = TooltipPrimitive.Provider;

/**
 * Short label for an icon-only control or a truncated value. Never put
 * essential information or interactive content in a tooltip.
 */
export function Tooltip({
  content,
  children,
  side = "top",
  delayDuration = 300,
}: {
  content: React.ReactNode;
  children: React.ReactElement;
  side?: "top" | "right" | "bottom" | "left";
  delayDuration?: number;
}) {
  if (!content) return children;
  return (
    <TooltipPrimitive.Provider delayDuration={delayDuration}>
      <TooltipPrimitive.Root>
        <TooltipPrimitive.Trigger asChild>{children}</TooltipPrimitive.Trigger>
        <TooltipPrimitive.Portal>
          <TooltipPrimitive.Content
            side={side}
            sideOffset={6}
            className={cn(
              "z-[70] max-w-xs rounded-md bg-navy-900 px-2 py-1 text-xs font-medium text-white shadow-md animate-in fade-in-0 zoom-in-95 dark:bg-slate-100 dark:text-navy-950",
            )}
          >
            {content}
          </TooltipPrimitive.Content>
        </TooltipPrimitive.Portal>
      </TooltipPrimitive.Root>
    </TooltipPrimitive.Provider>
  );
}

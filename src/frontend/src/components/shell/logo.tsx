import { cn } from "@/lib/utils";

/** The Solvix mark: three tilted squares in the brand navy, blue and cyan. */
export function SolvixMark({ className }: { className?: string }) {
  return (
    <svg viewBox="0 0 64 44" className={cn("h-6 w-auto", className)} aria-hidden focusable="false">
      <rect x="20" y="2" width="20" height="20" rx="3" transform="rotate(45 30 12)" fill="#22d3ee" />
      <rect x="4" y="18" width="20" height="20" rx="3" transform="rotate(45 14 28)" fill="#4c8dff" />
      <rect x="36" y="18" width="20" height="20" rx="3" transform="rotate(45 46 28)" fill="#2563eb" />
    </svg>
  );
}

export function SolvixWordmark({ className, compact = false }: { className?: string; compact?: boolean }) {
  return (
    <span className={cn("flex items-center gap-2.5", className)}>
      <SolvixMark />
      {compact ? null : (
        <span className="flex flex-col leading-none">
          <span className="text-[15px] font-semibold tracking-[0.14em]">SOLVIX</span>
          <span className="mt-1 text-[10px] font-medium uppercase tracking-[0.2em] opacity-60">EndPoint</span>
        </span>
      )}
    </span>
  );
}

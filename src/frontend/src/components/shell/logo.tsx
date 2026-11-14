import { cn } from "@/lib/utils";

/** The Solvix mark: three tilted squares in the brand oranges. */
export function SolvixMark({ className }: { className?: string }) {
  return (
    <svg viewBox="0 0 64 44" className={cn("h-7 w-auto", className)} aria-hidden focusable="false">
      <rect x="20" y="2" width="20" height="20" rx="2" transform="rotate(45 30 12)" fill="#FF7A1A" />
      <rect x="4" y="18" width="20" height="20" rx="2" transform="rotate(45 14 28)" fill="#FF4D00" />
      <rect x="36" y="18" width="20" height="20" rx="2" transform="rotate(45 46 28)" fill="#FFA23F" />
    </svg>
  );
}

export function SolvixWordmark({ className }: { className?: string }) {
  return (
    <span className={cn("flex items-center gap-2", className)}>
      <SolvixMark />
      <span className="text-base font-bold tracking-[0.18em]">SOLVIX</span>
    </span>
  );
}

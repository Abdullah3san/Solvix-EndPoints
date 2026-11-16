import { Skeleton } from "@/components/ui/skeleton";

/** Mirrors the dashboard layout so content doesn't jump when it arrives. */
export default function DashboardLoading() {
  return (
    <div aria-busy="true" aria-label="Loading overview">
      <div className="mb-6 space-y-2">
        <Skeleton className="h-8 w-40" />
        <Skeleton className="h-4 w-80 max-w-full" />
      </div>
      <div className="grid grid-cols-1 gap-3 sm:grid-cols-2 xl:grid-cols-4">
        {Array.from({ length: 8 }, (_, i) => (
          <div key={i} className="rounded-lg border border-border bg-card p-4">
            <div className="flex items-start justify-between">
              <Skeleton className="h-4 w-28" />
              <Skeleton className="size-8" />
            </div>
            <Skeleton className="mt-3 h-8 w-20" />
            <Skeleton className="mt-2 h-3.5 w-32" />
          </div>
        ))}
      </div>
      <div className="mt-4 grid gap-4 lg:grid-cols-2 xl:grid-cols-3">
        {Array.from({ length: 3 }, (_, i) => (
          <div key={i} className="rounded-lg border border-border bg-card p-5">
            <Skeleton className="h-4 w-36" />
            <Skeleton className="mt-2 h-3.5 w-52" />
            <div className="mt-6 space-y-3">
              <Skeleton className="h-4 w-11/12" />
              <Skeleton className="h-4 w-8/12" />
              <Skeleton className="h-4 w-9/12" />
              <Skeleton className="h-4 w-6/12" />
            </div>
          </div>
        ))}
      </div>
    </div>
  );
}

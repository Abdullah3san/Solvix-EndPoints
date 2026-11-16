import { Skeleton } from "@/components/ui/skeleton";

export default function DevicesLoading() {
  return (
    <div aria-busy="true" aria-label="Loading devices">
      <div className="mb-6 space-y-2">
        <Skeleton className="h-8 w-32" />
        <Skeleton className="h-4 w-96 max-w-full" />
      </div>
      <div className="overflow-hidden rounded-lg border border-border bg-card">
        <div className="flex gap-2 border-b border-border p-3">
          <Skeleton className="h-8 w-64" />
          <Skeleton className="h-8 w-24" />
          <Skeleton className="h-8 w-24" />
        </div>
        {Array.from({ length: 10 }, (_, i) => (
          <div key={i} className="flex items-center gap-6 border-b border-border px-3 py-3.5 last:border-0">
            <Skeleton className="size-4" />
            <Skeleton className="h-4 w-40" />
            <Skeleton className="h-4 w-28" />
            <Skeleton className="hidden h-4 w-36 md:block" />
            <Skeleton className="hidden h-4 w-24 lg:block" />
            <Skeleton className="ml-auto h-4 w-16" />
          </div>
        ))}
      </div>
    </div>
  );
}

import { Skeleton } from "@/components/ui/skeleton";

export default function AgentOverviewLoading() {
  return (
    <div aria-busy="true" aria-label="Loading agent overview">
      <div className="mb-6 space-y-2">
        <Skeleton className="h-8 w-48" />
        <Skeleton className="h-4 w-96 max-w-full" />
      </div>
      <div className="grid grid-cols-1 gap-3 sm:grid-cols-2 xl:grid-cols-4">
        {Array.from({ length: 4 }, (_, i) => (
          <Skeleton key={i} className="h-28" />
        ))}
      </div>
      <Skeleton className="mt-4 h-40" />
      <div className="mt-4 grid gap-4 lg:grid-cols-2">
        {Array.from({ length: 4 }, (_, i) => (
          <Skeleton key={i} className="h-60" />
        ))}
      </div>
    </div>
  );
}

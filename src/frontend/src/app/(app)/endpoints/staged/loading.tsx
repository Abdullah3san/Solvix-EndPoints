import { Skeleton } from "@/components/ui/skeleton";

export default function StagedLoading() {
  return (
    <div aria-busy="true" aria-label="Loading staged computers">
      <div className="mb-6 space-y-2">
        <Skeleton className="h-8 w-52" />
        <Skeleton className="h-4 w-[28rem] max-w-full" />
      </div>
      <div className="mb-4 grid gap-3 sm:grid-cols-3">
        {Array.from({ length: 3 }, (_, i) => (
          <Skeleton key={i} className="h-24" />
        ))}
      </div>
      <Skeleton className="h-96" />
    </div>
  );
}

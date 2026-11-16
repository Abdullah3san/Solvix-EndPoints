"use client";

import { ErrorState } from "@/components/ui/empty-state";

export default function AppError({ error, reset }: { error: Error & { digest?: string }; reset: () => void }) {
  return (
    <ErrorState
      className="mx-auto max-w-lg py-16"
      title="Something went wrong"
      description={
        <>
          {error.message || "An unexpected error occurred."}
          {error.digest ? <span className="mt-1 block font-mono text-xs">Reference: {error.digest}</span> : null}
        </>
      }
      onRetry={reset}
    />
  );
}

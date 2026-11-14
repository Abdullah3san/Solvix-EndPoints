"use client";

import { Button } from "@/components/ui/button";
import { Alert } from "@/components/ui/alert";

export default function AppError({ error, reset }: { error: Error & { digest?: string }; reset: () => void }) {
  return (
    <div className="mx-auto max-w-lg space-y-4 py-10">
      <Alert tone="error" title="Something went wrong">
        {error.message || "An unexpected error occurred."}
        {error.digest ? <span className="block font-mono text-xs">ref: {error.digest}</span> : null}
      </Alert>
      <Button onClick={reset}>Try again</Button>
    </div>
  );
}

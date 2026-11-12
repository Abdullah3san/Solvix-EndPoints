import { Lock } from "lucide-react";
import { Card, CardContent } from "@/components/ui/card";

export function NoAccess({ what = "this page" }: { what?: string }) {
  return (
    <Card>
      <CardContent className="flex flex-col items-center gap-2 p-10 text-center">
        <Lock className="h-8 w-8 text-muted-foreground" aria-hidden />
        <p className="font-medium">You do not have access to {what}.</p>
        <p className="text-sm text-muted-foreground">Ask an administrator of this workspace for a role that includes it.</p>
      </CardContent>
    </Card>
  );
}

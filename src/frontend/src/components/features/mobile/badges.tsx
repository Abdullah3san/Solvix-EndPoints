import { Smartphone, Tablet } from "lucide-react";
import { Badge } from "@/components/ui/badge";
import type { CommandState, ComplianceState, DeviceOwnership, DeviceState } from "@/lib/api/mdm-types";
import { commandStateLabel, commandStateTone, complianceLabel, complianceTone, ownershipLabel, stateLabel, stateTone } from "@/lib/mdm";

export const ComplianceBadge = ({ value }: { value: ComplianceState }) => <Badge tone={complianceTone[value]}>{complianceLabel[value]}</Badge>;
export const StateBadge = ({ value }: { value: DeviceState }) => <Badge tone={stateTone[value]}>{stateLabel[value]}</Badge>;
export const CommandStateBadge = ({ value }: { value: CommandState }) => <Badge tone={commandStateTone[value]}>{commandStateLabel[value]}</Badge>;

export function OwnershipBadge({ value }: { value: DeviceOwnership }) {
  return <Badge tone={value === "PersonallyOwned" ? "warning" : "neutral"}>{ownershipLabel[value]}</Badge>;
}

export function PlatformIcon({ platform }: { platform: "Android" | "Ios" }) {
  const Icon = platform === "Android" ? Smartphone : Tablet;
  return <Icon className="h-4 w-4 text-muted-foreground" aria-label={platform === "Android" ? "Android" : "iOS"} />;
}

import type { Metadata } from "next";
import { loadAgentOverview } from "@/lib/agent/overview";
import { AgentOverviewView } from "@/components/features/agent/agent-overview-view";

export const metadata: Metadata = { title: "Agent overview" };

export default async function AgentOverviewPage() {
  return <AgentOverviewView data={await loadAgentOverview()} />;
}

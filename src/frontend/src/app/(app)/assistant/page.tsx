import type { Metadata } from "next";
import { serverApi } from "@/lib/api/server";
import type { AssistantStatusDto } from "@/lib/api/types";
import { PageHeader } from "@/components/ui/page-header";
import { ChatView } from "@/components/features/assistant/chat-view";

export const metadata: Metadata = { title: "AI Assistant" };

export default async function AssistantPage() {
  const status = await serverApi<AssistantStatusDto>("assistant/status");
  return (
    <>
      <PageHeader title="AI Assistant" description="Ask about your computers in plain language. Actions are proposed for your approval, never run automatically." />
      <ChatView status={status} />
    </>
  );
}

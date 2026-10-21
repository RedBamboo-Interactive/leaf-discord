import type { MessageBlock } from "@redbamboo/chat"
import type { DiscordAuthor } from "./discord-transcript"

export type PublicNovaThread = {
  threadRef: string
  channelName: string
  title: string
  lastActivityAt: string
  lastSnippet: string
  unread: boolean | number
  scenario: string
}

export type PublicNovaMessage = {
  id: string
  at: string
  author: {
    kind: "envoy" | "self" | "human"
    name: string
    avatarUrl?: string
  }
  text: string
  outcome?: "posted" | "blocked" | "failed" | "fallback"
  reason?: string
}

export type PublicNovaConversation = {
  threadRef: string
  title: string
  channelName: string
  messages: PublicNovaMessage[]
}

export function projectPublicNovaMessages(source: PublicNovaMessage[]): {
  messages: MessageBlock[]
  authors: Record<string, DiscordAuthor>
} {
  const authors: Record<string, DiscordAuthor> = {}
  const messages: MessageBlock[] = []
  for (const message of source) {
    const authorId = `public-nova:${message.author.kind}:${message.author.name}`
    if (message.author.kind !== "self") {
      authors[authorId] = {
        name: message.author.name,
        avatarUrl: message.author.avatarUrl ?? "",
      }
    }
    messages.push({
      id: message.id,
      role: message.author.kind === "self" ? "assistant" : "user",
      senderAgentId: message.author.kind === "self" ? undefined : authorId,
      timestamp: message.at,
      parts: [{ type: "text", content: message.text }],
      metadata: { publicNova: true, outcome: message.outcome },
    })
    if (message.outcome && message.outcome !== "posted") {
      messages.push({
        id: `${message.id}:outcome`,
        role: "assistant",
        timestamp: message.at,
        parts: [{
          type: "text",
          content: `Public Nova ${message.outcome}${message.reason ? ` · ${message.reason}` : ""}`,
          phase: "commentary",
        }],
        metadata: { publicNovaSystemLine: true },
      })
    }
  }
  return { messages, authors }
}

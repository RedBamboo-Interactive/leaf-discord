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

export type PublicNovaOutcome = Exclude<NonNullable<PublicNovaMessage["outcome"]>, "posted">

export type PublicNovaOutcomeLine = {
  outcome: PublicNovaOutcome
  label: string
}

export function humanizePublicNovaReason(reason?: string): string {
  switch (reason) {
    case "wait_for_human":
      return "Paused until a human replies"
    case "absolute-path":
      return "Withheld: it contained a file path"
    case "relay_link_not_allowed":
      return "Withheld: it contained a link that isn't allowed"
    case "step_limit":
    case "repeated_action":
      return "Couldn't answer from the shared material"
    case undefined:
    case "":
      return "Withheld"
    default:
      return `Withheld (reason: ${reason})`
  }
}

export function publicNovaOutcomeLine(message: MessageBlock): PublicNovaOutcomeLine | null {
  const value = message.metadata?.publicNovaOutcome
  if (!value || typeof value !== "object") return null
  const outcome = (value as Record<string, unknown>).outcome
  const label = (value as Record<string, unknown>).label
  if ((outcome !== "blocked" && outcome !== "failed" && outcome !== "fallback") || typeof label !== "string")
    return null
  return { outcome, label }
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
    const outcome = message.outcome && message.outcome !== "posted"
      ? { outcome: message.outcome, label: humanizePublicNovaReason(message.reason) }
      : undefined
    messages.push({
      id: message.id,
      role: message.author.kind === "self" ? "assistant" : "user",
      senderAgentId: message.author.kind === "self" ? undefined : authorId,
      timestamp: message.at,
      parts: [{ type: "text", content: message.text }],
      metadata: { publicNova: true, outcome: message.outcome, publicNovaOutcome: outcome },
    })
  }
  return { messages, authors }
}

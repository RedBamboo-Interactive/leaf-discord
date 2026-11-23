// What the owner's transcript shows for a reply the outbound Sentinel kept off Discord, and the
// route that releases it. Kept free of UI imports so it can be tested on its own.

/** The outbound Sentinel review of one assistant reply, as the transcript route projects it. */
export type DiscordOutboundStatus = {
  outcome?: "delivered" | "held" | "withheld"
  risk?: string
  signals?: string[]
  handling?: string
  available?: boolean
  /** The reply is parked in the outbox and the owner can release it. */
  held?: boolean
  /** Set once the owner released it. */
  released?: { by?: string; at?: string }
}

type TranscriptRecord = {
  role: string
  kind: string
  messageUid: string
  metadata?: { discord?: { outboundSentinel?: DiscordOutboundStatus } }
}

/** Outbound reviews of the assistant replies in a transcript, by message uid. */
export function outboundStatuses(
  messages: readonly TranscriptRecord[],
): Record<string, DiscordOutboundStatus> {
  const statuses: Record<string, DiscordOutboundStatus> = {}
  for (const message of messages) {
    const status = message.metadata?.discord?.outboundSentinel
    if (message.role === "assistant" && message.kind === "text" && status)
      statuses[message.messageUid] = status
  }
  return statuses
}

/** The reply a rendered chat block stands for (a block's id is its first record's uid). */
export function blockMessageUid(block: { id: string; metadata?: Record<string, unknown> }): string {
  const uid = block.metadata?.messageUid
  return typeof uid === "string" && uid.length > 0 ? uid : block.id
}

export function releaseHeldReplyPath(bindingId: string, messageUid: string, discussionId?: string): string {
  const path = heldMessageActionPath(bindingId, messageUid, "release")
  return discussionId === undefined ? path : withDiscussion(path, discussionId)
}

export function discardHeldOutreachPath(bindingId: string, messageUid: string, discussionId: string): string {
  return withDiscussion(heldMessageActionPath(bindingId, messageUid, "discard"), discussionId)
}

export function outreachApprovalsPath(discussionId: string): string {
  return withDiscussion("/outreach/approvals", discussionId)
}

function heldMessageActionPath(bindingId: string, messageUid: string, action: "release" | "discard"): string {
  return `/bindings/${encodeURIComponent(bindingId)}/messages/${encodeURIComponent(messageUid)}/${action}`
}

function withDiscussion(path: string, discussionId: string): string {
  return `${path}?discussionId=${encodeURIComponent(discussionId)}`
}

/** Only image names in Leaf's Asset store can become preview requests. */
export function outreachAssetImagePath(assetId: string): string | null {
  const name = assetId.replace(/^\/api\/assets\//, "")
  return /^(?:stable-[a-f0-9]{64}|[a-f0-9]{8}(?:-[a-f0-9]{4}){3}-[a-f0-9]{12})\.(?:avif|gif|jpe?g|png|webp)$/i.test(name)
    ? `/api/assets/${name}`
    : null
}

/** Why the reply was kept back, in a sentence for the owner. */
export function heldReplyReason(status: DiscordOutboundStatus, subject = "reply"): string {
  if (status.available === false)
    return `The safety check was unavailable, so this ${subject} was not sent.`
  const signals = status.signals?.length ? ` (${status.signals.join(", ")})` : ""
  return `The Sentinel kept this ${subject} off Discord because it could have shared private information${signals}.`
}

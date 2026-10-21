import assert from "node:assert/strict"
import test from "node:test"
import {
  humanizePublicNovaReason,
  effectivePublicAgentId,
  projectPublicNovaMessages,
  publicThreadListSubtitle,
  publicThreadsForAgent,
  publicThreadsRequestUrl,
  publicNovaOutcomeLine,
  type PublicNovaMessage,
} from "./public-nova.ts"

test("All Agents requests every public thread and labels each with its Agent", () => {
  assert.equal(effectivePublicAgentId(null, [{ id: "agent-one" }]), null)
  assert.equal(effectivePublicAgentId(null, [{ id: "agent-one" }, { id: "agent-two" }]), null)
  assert.equal(publicThreadsRequestUrl(null), "/api/apps/leaf-commons/threads")

  const items = [
    { threadRef: "one", agentId: "agent-one", agentName: "Atlas", channelName: "bugs", title: "One", lastActivityAt: "now", lastSnippet: "First", unread: false, scenario: "bug" },
    { threadRef: "two", agentId: "agent-two", agentName: "Scout", channelName: "showcase", title: "Two", lastActivityAt: "now", lastSnippet: "Second", unread: false, scenario: "showcase" },
  ]
  assert.deepEqual(publicThreadsForAgent(items, null), items)
  assert.equal(publicThreadListSubtitle(items[1], true), "Scout · #showcase · Second")
})

test("A selected Agent requests and retains only that Agent's public threads", () => {
  assert.equal(effectivePublicAgentId("agent-two", [{ id: "agent-one" }, { id: "agent-two" }]), "agent-two")
  assert.equal(publicThreadsRequestUrl("agent / two"), "/api/apps/leaf-commons/threads?agentId=agent%20%2F%20two")

  const items = [
    { threadRef: "one", agentId: "agent-one", agentName: "Atlas", channelName: "bugs", title: "One", lastActivityAt: "now", lastSnippet: "", unread: false, scenario: "bug" },
    { threadRef: "two", agentId: "agent-two", agentName: "Scout", channelName: "showcase", title: "Two", lastActivityAt: "now", lastSnippet: "", unread: false, scenario: "showcase" },
  ]
  assert.deepEqual(publicThreadsForAgent(items, "agent-two"), [items[1]])
  assert.equal(publicThreadListSubtitle(items[1], false), "#showcase · ")
})

test("public Agent reason codes use human-facing copy", () => {
  assert.equal(humanizePublicNovaReason("wait_for_human"), "Paused until a human replies")
  assert.equal(humanizePublicNovaReason("absolute-path"), "Withheld: it contained a file path")
  assert.equal(humanizePublicNovaReason("relay_link_not_allowed"), "Withheld: it contained a link that isn't allowed")
  assert.equal(humanizePublicNovaReason("step_limit"), "Couldn't answer from the shared material")
  assert.equal(humanizePublicNovaReason("repeated_action"), "Couldn't answer from the shared material")
  assert.equal(humanizePublicNovaReason("future_reason"), "Withheld (reason: future_reason)")
})

test("public Agent attribution matches the Discord transcript model", () => {
  const source: PublicNovaMessage[] = [
    { id: "envoy", at: "2026-10-21T12:00:00Z", author: { kind: "envoy", name: "Nova · Lab", avatarUrl: "https://relay.example/avatar/lab" }, text: "From another Nova" },
    { id: "human", at: "2026-10-21T12:01:00Z", author: { kind: "human", name: "Lynial", avatarUrl: "https://cdn.discordapp.com/avatar" }, text: "From Discord" },
    { id: "self", at: "2026-10-21T12:02:00Z", author: { kind: "self", name: "Nova · Lynial", avatarUrl: "https://relay.example/avatar/self" }, text: "From our Nova", outcome: "posted" },
  ]
  const projection = projectPublicNovaMessages(source)

  assert.equal(projection.messages[0].role, "user")
  assert.deepEqual(projection.authors[projection.messages[0].senderAgentId!], {
    name: "Nova · Lab",
    avatarUrl: "https://relay.example/avatar/lab",
  })
  assert.equal(projection.messages[1].role, "user")
  assert.equal(projection.authors[projection.messages[1].senderAgentId!].name, "Lynial")
  assert.equal(projection.messages[2].role, "assistant")
  assert.equal(projection.messages[2].senderAgentId, undefined)
  assert.equal(projection.authors["public-nova:self:Nova · Lynial"], undefined)
})

test("blocked, failed, and fallback outcomes stay attached as native status lines", () => {
  const projection = projectPublicNovaMessages([
    {
      id: "blocked",
      at: "2026-10-21T12:00:00Z",
      author: { kind: "self", name: "Nova · Lynial" },
      text: "A reply was withheld.",
      outcome: "blocked",
      reason: "wait_for_human",
    },
    {
      id: "failed",
      at: "2026-10-21T12:01:00Z",
      author: { kind: "self", name: "Nova · Lynial" },
      text: "A reply failed.",
      outcome: "failed",
      reason: "absolute-path",
    },
    {
      id: "fallback",
      at: "2026-10-21T12:02:00Z",
      author: { kind: "self", name: "Nova · Lynial" },
      text: "A safe fallback was posted.",
      outcome: "fallback",
      reason: "step_limit",
    },
  ])

  assert.equal(projection.messages.length, 3)
  assert.deepEqual(publicNovaOutcomeLine(projection.messages[0]), {
    outcome: "blocked",
    label: "Paused until a human replies",
  })
  assert.deepEqual(publicNovaOutcomeLine(projection.messages[1]), {
    outcome: "failed",
    label: "Withheld: it contained a file path",
  })
  assert.deepEqual(publicNovaOutcomeLine(projection.messages[2]), {
    outcome: "fallback",
    label: "Couldn't answer from the shared material",
  })
  assert.equal(projection.messages[0].parts[0].content, "A reply was withheld.")
})

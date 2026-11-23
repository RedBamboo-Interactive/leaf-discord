import assert from "node:assert/strict"
import test from "node:test"
import {
  discardHeldOutreachPath,
  outreachApprovalsPath,
  outreachAssetImagePath,
  blockMessageUid,
  heldReplyReason,
  outboundStatuses,
  releaseHeldReplyPath,
} from "./discord-held-reply.ts"

test("outreach approval actions scope and encode the origin discussion", () => {
  assert.equal(outreachApprovalsPath("a/b + c"), "/outreach/approvals?discussionId=a%2Fb%20%2B%20c")
  assert.equal(releaseHeldReplyPath("binding", "outreach", "origin"),
    "/bindings/binding/messages/outreach/release?discussionId=origin")
  assert.equal(discardHeldOutreachPath("binding", "outreach", "origin"),
    "/bindings/binding/messages/outreach/discard?discussionId=origin")
})

test("previews accept only canonical local Leaf image asset identifiers", () => {
  const image = "019a8140-fcae-4887-911d-eb24c2c787e4.webp"
  assert.equal(outreachAssetImagePath(image), `/api/assets/${image}`)
  assert.equal(outreachAssetImagePath(`/api/assets/${image}`), `/api/assets/${image}`)
  assert.equal(outreachAssetImagePath(`stable-${"a".repeat(64)}.png`), `/api/assets/stable-${"a".repeat(64)}.png`)
  for (const value of [`https://example.org/${image}`, `//example.org/${image}`, `../${image}`,
    `/api/assets/${image}?redirect=1`, `/api/assets/%2f${image}`, image.replace(".webp", ".svg")])
    assert.equal(outreachAssetImagePath(value), null)
})

test("only assistant text replies with a review are listed, by message uid", () => {
  const statuses = outboundStatuses([
    { role: "user", kind: "text", messageUid: "u1", metadata: { discord: { outboundSentinel: { held: true } } } },
    { role: "assistant", kind: "text", messageUid: "a1", metadata: { discord: { outboundSentinel: { outcome: "held", held: true } } } },
    { role: "assistant", kind: "text", messageUid: "a2" },
    { role: "assistant", kind: "commentary", messageUid: "a3", metadata: { discord: { outboundSentinel: { held: true } } } },
    { role: "assistant", kind: "text", messageUid: "a4", metadata: { discord: { outboundSentinel: { outcome: "held", released: { by: "someone", at: "2026-11-10T10:00:00Z" } } } } },
  ])
  assert.deepEqual(Object.keys(statuses), ["a1", "a4"])
  assert.equal(statuses.a1.held, true)
  assert.equal(statuses.a4.held, undefined)
  assert.equal(statuses.a4.released?.by, "someone")
})

test("a chat block maps back to its reply uid", () => {
  assert.equal(blockMessageUid({ id: "reply-1" }), "reply-1")
  assert.equal(blockMessageUid({ id: "reply-1:segment:1", metadata: { messageUid: "reply-1" } }), "reply-1")
  assert.equal(blockMessageUid({ id: "db-3", metadata: { messageUid: "" } }), "db-3")
})

test("the release route encodes the binding and the reply uid", () => {
  assert.equal(
    releaseHeldReplyPath("b-1", "reply / 1#x"),
    "/bindings/b-1/messages/reply%20%2F%201%23x/release",
  )
})

test("the reason tells an unavailable check from a hold", () => {
  assert.match(heldReplyReason({ available: false }), /unavailable/)
  assert.match(heldReplyReason({ available: true, signals: ["personal-data", "credential"] }), /personal-data, credential/)
  assert.doesNotMatch(heldReplyReason({ available: true }), /\(\)/)
})

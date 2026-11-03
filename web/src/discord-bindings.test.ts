import assert from "node:assert/strict"
import test from "node:test"
import {
  DISCORD_BINDING_PAGE_SIZE,
  discordBindingsRequestPath,
} from "./discord-bindings.ts"

test("binding inventory always requests one bounded page", () => {
  assert.equal(DISCORD_BINDING_PAGE_SIZE, 24)
  assert.equal(discordBindingsRequestPath(null, null), "/bindings?limit=24")
  assert.equal(
    discordBindingsRequestPath("agent / two", null),
    "/bindings?limit=24&agentId=agent+%2F+two",
  )
  assert.equal(
    discordBindingsRequestPath("agent-two", "opaque+/="),
    "/bindings?limit=24&agentId=agent-two&cursor=opaque%2B%2F%3D",
  )
})

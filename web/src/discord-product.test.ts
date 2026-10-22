import assert from "node:assert/strict"
import test from "node:test"
import { readFileSync } from "node:fs"
import { fileURLToPath } from "node:url"
import { DISCORD_PRODUCT_COPY } from "./discord-product.ts"

test("Discord presents the two products with the decided names", () => {
  assert.equal(DISCORD_PRODUCT_COPY.communityTitle, "Leaf Community")
  assert.equal(DISCORD_PRODUCT_COPY.communitySubtitle, "Your Agents in the Leaf server")
  assert.equal(DISCORD_PRODUCT_COPY.conversationsTitle, "Servers & messages")
  assert.equal(DISCORD_PRODUCT_COPY.conversationsSubtitle, "Your bots: servers, threads and DMs")
  assert.equal(DISCORD_PRODUCT_COPY.communitySettingsHref, "/extensions/leaf-commons")
})

test("retired product terms are absent from rendered leaf-discord copy", () => {
  const files = ["plugin.tsx", "DiscordSettingsPanel.tsx", "discord-product.ts"]
  const renderedSources = files.map((file) => readFileSync(
    fileURLToPath(new URL(file, import.meta.url)), "utf8"))
  for (const source of renderedSources) {
    assert.doesNotMatch(source, /Public Nova/)
    assert.doesNotMatch(source, />[^<]*(?:Commons|Envoy)[^<]*</)
  }
})

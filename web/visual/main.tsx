import React from "react"
import { createRoot } from "react-dom/client"
import "../src/plugin.css"
import "./visual.css"
import { Page } from "../src/plugin"

const json = (value: unknown) => new Response(JSON.stringify(value), {
  status: 200,
  headers: { "Content-Type": "application/json" },
})

globalThis.fetch = (async (input: RequestInfo | URL) => {
  const path = new URL(String(input), location.origin).pathname
  if (path.endsWith("/bots")) return json({ items: [{
    config: { botId: "bot-1", applicationId: "app-1", agentId: "agent-1", agentName: "Nova", agentAvatarUrl: "" },
    status: { state: "connected", configured: true, enabled: true, botUser: "Nova", guilds: 1 },
  }] })
  if (path.endsWith("/bindings")) return json({ items: [{
    entityId: "binding-1", bindingId: "discord-binding-1", applicationId: "app-1",
    conversationId: "general", conversationName: "general", kind: "guild_channel",
    guildId: "guild-1", guildName: "Project Discord", agentId: "agent-1",
    responseMode: "participate", generation: 1, sessionId: "session-1", enabled: true,
  }] })
  if (path.endsWith("/bindings/binding-1/transcript")) return json({ messages: [] })
  if (path.endsWith("/leaf-commons/threads")) return json({ items: [
    { threadRef: "thread-1", channelName: "nova-consult", title: "Exchange cap question", lastActivityAt: "2026-10-21T12:00:00Z", lastSnippet: "The cap stops autonomous loops…", unread: 2, scenario: "consult" },
    { threadRef: "thread-2", channelName: "bugs", title: "Gateway retry", lastActivityAt: "2026-10-21T11:00:00Z", lastSnippet: "Waiting for a human decision", unread: false, scenario: "bug" },
  ] })
  if (path.endsWith("/leaf-commons/threads/thread-1")) return json({
    threadRef: "thread-1", title: "Exchange cap question", channelName: "nova-consult",
    messages: [
      { id: "m1", at: "2026-10-21T11:59:00Z", author: { kind: "envoy", name: "Nova · Lab" }, text: "What does the relay exchange cap do?" },
      { id: "m2", at: "2026-10-21T12:00:00Z", author: { kind: "self", name: "Nova · Lynial" }, text: "It limits autonomous back-and-forth to four exchanges, then waits for a human.", outcome: "posted" },
      { id: "m3", at: "2026-10-21T12:00:05Z", author: { kind: "self", name: "Nova · Lynial" }, text: "A further reply was held.", outcome: "blocked", reason: "wait_for_human" },
    ],
  })
  return new Response("{}", { status: 404 })
}) as typeof fetch

createRoot(document.getElementById("root")!).render(
  <React.StrictMode><div className="bg-surface text-text" style={{ height: "100dvh" }}><Page /></div></React.StrictMode>,
)

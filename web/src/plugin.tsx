// Canonical RedLeaf chat application surface.
import { useCallback, useEffect, useState } from "react"
import { ChatPanel, ChatStatusLine, type MessageBlock } from "@redbamboo/chat"
import {
  Button,
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuTrigger,
  ItemList,
  ItemListRow,
  MasterDetailLayout,
  PanelHeader,
} from "@redbamboo/ui"
import type { LeafAppPlugin } from "@redbamboo/utility"
import { DiscordSettingsPanel } from "./DiscordSettingsPanel"
import { DiscordBehaviorPanel } from "./DiscordBehaviorPanel"
import {
  projectDiscordTranscript,
  type DiscordAuthor,
  type DiscordTranscriptMessage,
} from "./discord-transcript"
import {
  effectivePublicAgentId,
  projectPublicNovaMessages,
  publicThreadListSubtitle,
  publicThreadsForAgent,
  publicThreadsRequestUrl,
  publicNovaOutcomeLine,
  type PublicNovaConversation,
  type PublicNovaThread,
} from "./public-nova"

function PublicNovaOutcomeStatus({ block }: { block: MessageBlock }) {
  const line = publicNovaOutcomeLine(block)
  if (!line) return null
  const appearance = line.outcome === "failed"
    ? { color: "var(--color-accent-red)", icon: "ph-bold ph-warning-circle" }
    : line.outcome === "blocked"
      ? { color: "var(--color-accent-gold)", icon: "ph-bold ph-shield-warning" }
      : { color: "var(--color-text-muted)", icon: "ph-bold ph-info" }
  return (
    <div role="status" aria-label={`Public Agent ${line.outcome}`} className="mt-1">
      <ChatStatusLine color={appearance.color} icon={appearance.icon} label={line.label} />
    </div>
  )
}

type BotStatus = {
  state: string
  configured: boolean
  enabled: boolean
  botUser?: string
  guilds: number
  connectedAt?: string
  lastError?: string
  profileWarning?: string
}

type Config = {
  botId: string
  applicationId: string
  agentId: string
  agentName?: string
  agentAvatarUrl?: string
}

type Bot = {
  config: Config
  status: BotStatus
}

type Binding = {
  entityId: string
  bindingId: string
  applicationId: string
  conversationId: string
  conversationName: string
  kind: string
  guildId?: string
  guildName?: string
  agentId: string
  responseMode: string
  generation: number
  sessionId?: string
  enabled: boolean
  iconUrl?: string
}

async function api<T>(path: string, init?: RequestInit): Promise<T> {
  const response = await fetch(`/api/apps/leaf-discord${path}`, {
    ...init,
    headers: init?.body ? { "Content-Type": "application/json", ...init.headers } : init?.headers,
  })
  const payload = await response.json().catch(() => ({}))
  if (!response.ok) throw new Error(payload.message ?? payload.error ?? `HTTP ${response.status}`)
  return payload as T
}

export function Page() {
  const [bots, setBots] = useState<Bot[]>([])
  const [bindings, setBindings] = useState<Binding[]>([])
  const [selectedBindingId, setSelectedBindingId] = useState<string | null>(null)
  const [publicThreads, setPublicThreads] = useState<PublicNovaThread[] | null>(null)
  const [selectedPublicRef, setSelectedPublicRef] = useState<string | null>(null)
  const [agentFilter, setAgentFilterState] = useState<string | null>(() =>
    window.localStorage.getItem("leaf-discord-agent-filter"))
  const [mobileTab, setMobileTab] = useState(0)
  const [configuring, setConfiguring] = useState(false)
  const [messages, setMessages] = useState<MessageBlock[]>([])
  const [authors, setAuthors] = useState<Record<string, DiscordAuthor>>({})
  const [transcriptLoading, setTranscriptLoading] = useState(false)
  const [busy, setBusy] = useState(false)
  const [notice, setNotice] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)

  const refresh = useCallback(async () => {
    const [botResult, bindingResult] = await Promise.all([
      api<{ items: Bot[] }>("/bots"),
      api<{ items: Binding[] }>("/bindings"),
    ])
    const publicAgentId = effectivePublicAgentId(
      agentFilter,
      botResult.items.map((bot) => ({ id: bot.config.agentId })),
    )
    const publicResult = await fetch(publicThreadsRequestUrl(publicAgentId)).then(async (response) => {
        if (response.status === 404 || response.status === 403) return null
        if (!response.ok) throw new Error(`Public Agent HTTP ${response.status}`)
        return await response.json() as { items: PublicNovaThread[] }
      }).catch(() => null)
    setBots(botResult.items)
    setBindings(bindingResult.items)
    setPublicThreads(publicResult
      ? publicThreadsForAgent(publicResult.items, publicAgentId)
      : null)
  }, [agentFilter])

  useEffect(() => {
    void refresh().catch((reason) => setError(String(reason.message ?? reason)))
    const timer = window.setInterval(() => void refresh().catch(() => {}), 5000)
    return () => window.clearInterval(timer)
  }, [refresh])

  const loadTranscript = useCallback(async (bindingId: string, showSpinner = false) => {
    if (showSpinner) setTranscriptLoading(true)
    try {
      const result = await api<{ messages: DiscordTranscriptMessage[] }>(`/bindings/${bindingId}/transcript`)
      const projection = projectDiscordTranscript(result.messages)
      setMessages(projection.messages)
      setAuthors(projection.authors)
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : String(reason))
    } finally {
      if (showSpinner) setTranscriptLoading(false)
    }
  }, [])

  const loadPublicTranscript = useCallback(async (threadRef: string, showSpinner = false) => {
    if (showSpinner) setTranscriptLoading(true)
    try {
      const response = await fetch(`/api/apps/leaf-commons/threads/${encodeURIComponent(threadRef)}`)
      const payload = await response.json().catch(() => ({}))
      if (!response.ok) throw new Error(payload.message ?? payload.error ?? `HTTP ${response.status}`)
      const projection = projectPublicNovaMessages((payload as PublicNovaConversation).messages)
      setMessages(projection.messages)
      setAuthors(projection.authors)
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : String(reason))
    } finally {
      if (showSpinner) setTranscriptLoading(false)
    }
  }, [])

  useEffect(() => {
    if (!selectedBindingId || selectedPublicRef) {
      setMessages([])
      setAuthors({})
      return
    }
    void loadTranscript(selectedBindingId, true)
    const timer = window.setInterval(() => void loadTranscript(selectedBindingId), 2500)
    return () => window.clearInterval(timer)
  }, [loadTranscript, selectedBindingId, selectedPublicRef])

  useEffect(() => {
    if (!selectedPublicRef) return
    void loadPublicTranscript(selectedPublicRef, true)
    const timer = window.setInterval(() => void loadPublicTranscript(selectedPublicRef), 2500)
    return () => window.clearInterval(timer)
  }, [loadPublicTranscript, selectedPublicRef])

  const resolveDiscordAuthor = useCallback(
    (id: string) => authors[id],
    [authors],
  )

  const selectBinding = useCallback((id: string) => {
    setSelectedBindingId(id)
    setSelectedPublicRef(null)
    setMobileTab(1)
  }, [])

  const selectPublicThread = useCallback((threadRef: string) => {
    setSelectedPublicRef(threadRef)
    setSelectedBindingId(null)
    setConfiguring(false)
    setMobileTab(1)
  }, [])

  const setAgentFilter = useCallback((id: string | null) => {
    setAgentFilterState(id)
    setPublicThreads(null)
    setSelectedPublicRef(null)
    setMessages([])
    setAuthors({})
    if (id) window.localStorage.setItem("leaf-discord-agent-filter", id)
    else window.localStorage.removeItem("leaf-discord-agent-filter")
  }, [])

  const visibleBindings = agentFilter
    ? bindings.filter((binding) => binding.agentId === agentFilter)
    : bindings

  useEffect(() => {
    if (selectedPublicRef) return
    setSelectedBindingId((current) => current
      && visibleBindings.some((binding) => binding.entityId === current)
        ? current : visibleBindings[0]?.entityId ?? null)
  }, [agentFilter, bindings, selectedPublicRef])

  useEffect(() => {
    if (agentFilter && !bots.some((bot) => bot.config.agentId === agentFilter))
      setAgentFilter(null)
  }, [agentFilter, bots, setAgentFilter])

  async function clearBinding(binding: Binding) {
    setBusy(true)
    setError(null)
    try {
      const cleared = await api<Binding>(`/bindings/${binding.entityId}/clear`, { method: "POST" })
      await refresh()
      setSelectedBindingId(cleared.entityId)
      await loadTranscript(cleared.entityId, true)
      setNotice(`Conversation ${binding.conversationId} now has a fresh generation.`)
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : String(reason))
    } finally {
      setBusy(false)
    }
  }

  if (!bots) {
    return <div className="flex h-full items-center justify-center text-sm text-text-muted">Loading Discord bridge...</div>
  }

  const selectedBinding = visibleBindings.find((binding) => binding.entityId === selectedBindingId) ?? null
  const selectedPublicThread = publicThreads?.find((thread) => thread.threadRef === selectedPublicRef) ?? null
  const publicAgentId = effectivePublicAgentId(agentFilter, bots.map((bot) => ({ id: bot.config.agentId })))
  const selectedBot = selectedPublicThread
    ? bots.find((bot) => bot.config.agentId === selectedPublicThread.agentId)
    : selectedBinding
    ? bots.find((bot) => bot.config.applicationId === selectedBinding.applicationId)
    : agentFilter
      ? bots.find((bot) => bot.config.agentId === agentFilter)
      : bots[0]
  const avatarUrl = selectedBot?.config.agentAvatarUrl ?? ""
  const visibleError = error ?? selectedBot?.status.lastError ?? selectedBot?.status.profileWarning
  const agents = bots.map((bot) => ({
    id: bot.config.agentId,
    name: bot.config.agentName ?? bot.config.agentId,
    avatarUrl: bot.config.agentAvatarUrl,
  }))

  const chatHeader = selectedPublicThread ? (
    <PanelHeader>
      <div className="flex min-w-0 flex-1 flex-col">
        <span className="truncate text-[14px] font-medium text-contrast">{selectedPublicThread.title}</span>
        <span className="truncate text-xs text-text-muted">
          {selectedPublicThread.agentName} public / #{selectedPublicThread.channelName} / {selectedPublicThread.scenario}
        </span>
      </div>
    </PanelHeader>
  ) : selectedBinding ? (
    <PanelHeader>
      <div className="flex min-w-0 flex-1 flex-col">
        <span className="truncate text-[14px] font-medium text-contrast">{selectedBinding.conversationName}</span>
        <span className="truncate text-xs text-text-muted">
          {selectedBinding.guildName ? `${selectedBinding.guildName} / ` : ""}
          {selectedBinding.kind.replace("_", " ")} / generation {selectedBinding.generation}
          {selectedBinding.sessionId ? ` / session ${selectedBinding.sessionId}` : ""}
        </span>
      </div>
      <Button variant="ghost" size="sm" disabled={busy} onClick={() => clearBinding(selectedBinding)}>
        <i className="ph-bold ph-arrow-counter-clockwise text-xs" />
        Clear
      </Button>
    </PanelHeader>
  ) : null

  const chatArea = !selectedBinding && !selectedPublicThread ? (
    <div className="flex flex-1 items-center justify-center text-text-muted">
      <div className="text-center">
        <i className="ph-bold ph-discord-logo mb-3 text-3xl opacity-30" />
        <p className="text-sm">Select a Discord discussion.</p>
      </div>
    </div>
  ) : transcriptLoading && messages.length === 0 ? (
    <div className="flex flex-1 items-center justify-center text-text-muted">
      <div className="text-center">
        <i className="ph-bold ph-spinner mb-3 animate-spin text-2xl opacity-40" />
        <p className="text-sm">Loading canonical transcript...</p>
      </div>
    </div>
  ) : (
    <ChatPanel
      messages={messages}
      isStreaming={false}
      isReconnecting={false}
      interrupting={false}
      resumePending={false}
      sessionId={selectedBinding?.sessionId ?? selectedBinding?.entityId ?? selectedPublicThread!.threadRef}
      persistQueue={false}
      disabled
      hideComposer
      header={chatHeader}
      assistantAvatar={avatarUrl}
      resolveAgentInfo={resolveDiscordAuthor}
      renderMessageExtra={selectedPublicThread
        ? (block) => <PublicNovaOutcomeStatus block={block} />
        : undefined}
    />
  )

  const sidebar = (
    <>
      <PanelHeader title="Discord discussions">
        <AgentFilter
          agents={agents}
          selectedId={agentFilter}
          onSelect={setAgentFilter}
        />
        <div
          role="status"
          aria-label={`Discord ${selectedBot?.status.state ?? "not configured"}`}
          title={`Discord ${selectedBot?.status.state ?? "not configured"}`}
          className="relative size-9 shrink-0"
        >
          {avatarUrl
            ? <img src={avatarUrl} alt={`${selectedBot?.config.agentName ?? "Agent"} Discord bot`} className="size-9 rounded-full object-cover" />
            : <span className="flex size-9 items-center justify-center rounded-full bg-overlay-6 text-text-muted"><i className="ph-bold ph-robot" /></span>}
          <span className={`absolute -bottom-0.5 -right-1 flex h-4 min-w-5 items-center justify-center rounded-full border-2 border-surface px-1 text-[9px] text-white ${selectedBot?.status.state === "connected" ? "bg-accent-teal" : selectedBot?.status.state === "error" ? "bg-destructive" : "bg-text-disabled"}`}>
            <i className="ph-fill ph-discord-logo" aria-hidden="true" />
          </span>
        </div>
        <Button
          variant={configuring ? "secondary" : "ghost"}
          size="icon"
          disabled={!selectedBot}
          aria-label={`Configure ${selectedBot?.config.agentName ?? "Discord Agent"} behavior`}
          title="Configure Agent behavior"
          onClick={() => {
            setConfiguring(true)
            setMobileTab(1)
          }}
        >
          <i className="ph-bold ph-sliders-horizontal text-sm" />
        </Button>
      </PanelHeader>
      {(notice || visibleError) && (
        <div className={`border-b px-4 py-2 text-xs ${visibleError ? "border-destructive-a20 bg-destructive-a10 text-destructive" : "border-accent-teal-a20 bg-accent-teal-a10 text-accent-teal"}`}>
          {visibleError ?? notice}
        </div>
      )}
      <div className="min-h-0 flex-1 overflow-hidden">
        <div className="h-full overflow-y-auto">
          {publicThreads && <section aria-label={publicAgentId
            ? `${selectedBot?.config.agentName ?? "Agent"} public discussions`
            : "All Agents public discussions"}>
            <div className="px-4 pb-1 pt-3 text-[11px] font-semibold uppercase tracking-wide text-text-muted">
              {publicAgentId
                ? `Public ${publicThreads[0]?.agentName ?? selectedBot?.config.agentName ?? "Agent"}`
                : "Public"}
            </div>
            <ItemList
              items={publicThreads}
              keyFn={(thread) => thread.threadRef}
              emptyMessage="Public threads appear here after this Agent posts or is pinged."
              renderItem={(thread) => <ItemListRow
                selected={thread.threadRef === selectedPublicRef}
                onClick={() => selectPublicThread(thread.threadRef)}
                icon={<i className="ph-bold ph-globe-hemisphere-west" />}
                title={thread.title}
                subtitle={publicThreadListSubtitle(thread, publicAgentId === null)}
                trailing={Boolean(thread.unread)
                  ? <span className="min-w-5 rounded-full bg-primary px-1.5 text-center text-[10px] text-primary-foreground">{typeof thread.unread === "number" ? thread.unread : "•"}</span>
                  : null}
              />}
            />
          </section>}
          <section aria-label="Agent Discord discussions">
            <div className="px-4 pb-1 pt-3 text-[11px] font-semibold uppercase tracking-wide text-text-muted">Agent conversations</div>
            <ItemList
              items={visibleBindings}
              keyFn={(binding) => binding.entityId}
              emptyMessage="A discussion appears after its first eligible Discord message."
              renderItem={(binding) => (
                <ItemListRow
                  selected={binding.entityId === selectedBindingId}
                  onClick={() => selectBinding(binding.entityId)}
                  icon={binding.iconUrl
                    ? <img src={binding.iconUrl} alt="" className="size-full rounded-full object-cover" />
                    : <i className={binding.kind === "dm" ? "ph-bold ph-user" : binding.kind === "thread" ? "ph-bold ph-chats-teardrop" : "ph-bold ph-hash"} />}
                  title={binding.conversationName}
                  subtitle={binding.guildName
                    ? `${binding.guildName} / ${binding.kind.replace("_", " ")}`
                    : binding.kind.replace("_", " ")}
                  trailing={<span className={`size-2 rounded-full ${binding.sessionId ? "bg-accent-teal" : "bg-overlay-15"}`} title={binding.sessionId ? "Persistent session attached; resumes on demand" : "Waiting for first message"} />}
                />
              )}
            />
          </section>
        </div>
      </div>
    </>
  )

  return (
    <div className="h-full min-h-0">
      <MasterDetailLayout
        layoutKey="leaf-discord-discussions"
        mobileLabels={["Discussions", "Chat"]}
        mobileTab={mobileTab}
        onMobileTabChange={setMobileTab}
        sidebar={sidebar}
        detail={configuring && selectedBot
          ? <DiscordBehaviorPanel
              botId={selectedBot.config.botId}
              onClose={() => setConfiguring(false)}
            />
          : chatArea}
      />
    </div>
  )
}

function AgentFilter({ agents, selectedId, onSelect }: {
  agents: Array<{ id: string; name: string; avatarUrl?: string }>
  selectedId: string | null
  onSelect: (id: string | null) => void
}) {
  const unique = Array.from(new Map(agents.map((agent) => [agent.id, agent])).values())
  const selected = unique.find((agent) => agent.id === selectedId)
  if (unique.length < 2) return null
  return <DropdownMenu>
    <DropdownMenuTrigger className="flex items-center gap-1.5 rounded-md px-2 py-1 text-xs font-medium text-text-muted transition-colors hover:bg-overlay-5 hover:text-text">
      {selected?.avatarUrl
        ? <img src={selected.avatarUrl} alt="" className="size-4 rounded-full object-cover" />
        : <i className="ph-bold ph-users text-[10px]" />}
      <span>{selected?.name ?? "All agents"}</span>
      <i className="ph-bold ph-caret-down text-[8px] opacity-50" />
    </DropdownMenuTrigger>
    <DropdownMenuContent align="start" sideOffset={4}>
      <DropdownMenuItem onClick={() => onSelect(null)} className={!selectedId ? "text-primary" : ""}>
        <i className="ph-bold ph-users size-4 text-center" />
        All agents
      </DropdownMenuItem>
      {unique.map((agent) => <DropdownMenuItem
        key={agent.id}
        onClick={() => onSelect(agent.id)}
        className={selectedId === agent.id ? "text-primary" : ""}
      >
        {agent.avatarUrl
          ? <img src={agent.avatarUrl} alt="" className="size-4 rounded-full object-cover" />
          : <i className="ph-bold ph-user-circle size-4 text-center" />}
        {agent.name}
      </DropdownMenuItem>)}
    </DropdownMenuContent>
  </DropdownMenu>
}

export const plugin: LeafAppPlugin = {
  id: "leaf-discord",
  extensions: { discord: Page },
  settingsPanel: { Component: DiscordSettingsPanel },
}

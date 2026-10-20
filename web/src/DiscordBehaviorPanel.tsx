import { useCallback, useEffect, useMemo, useState } from "react"
import {
  Badge,
  Button,
  Dialog,
  DialogContent,
  DialogDescription,
  DialogHeader,
  DialogTitle,
  PanelHeader,
} from "@redbamboo/ui"

type Behavior = {
  entityId?: string
  botEntityId: string
  scope: "default" | "guild" | "channel" | "dm_default" | "dm"
  guildId?: string
  conversationId?: string
  responseMode: ResponseMode
  promptEntityId?: string
  instructions: string
  enabled: boolean
  persisted: boolean
}

type ResponseMode = "direct" | "selective" | "participate" | "context_only"

type GuildChannel = {
  id: string
  name: string
  kind: string
  parent_id?: string
  topic?: string
  can_read_history: boolean
  can_post: boolean
  can_reply: boolean
}

type GuildMap = {
  guild: { id: string; name: string }
  categories: Array<{ id: string; name: string; position: number }>
  channels: GuildChannel[]
}

type Dm = {
  entityId: string
  conversationId: string
  conversationName: string
  counterpartyUserId?: string
  enabled: boolean
}

type Configuration = {
  bot: {
    id: string
    applicationId: string
    agentId: string
    agentName: string
    agentAvatarUrl?: string
  }
  behaviors: Behavior[]
  navigation: GuildMap[]
  visibilityError?: string
  dms: Dm[]
}

type ScopeTarget = {
  scope: Behavior["scope"]
  title: string
  subtitle: string
  guildId?: string
  conversationId?: string
  inherited?: Behavior
}

const MODE_LABELS: Record<ResponseMode, { label: string; description: string }> = {
  direct: {
    label: "When addressed",
    description: "Guild messages require a mention or reply. Direct messages are admitted normally.",
  },
  selective: {
    label: "Selective",
    description: "The Agent sees every message and decides whether speaking adds value.",
  },
  participate: {
    label: "Participate",
    description: "Every human message starts an Agent turn and normally receives a reply.",
  },
  context_only: {
    label: "Do not engage",
    description: "Messages advance Discord delivery state without starting an Agent turn.",
  },
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

export function DiscordBehaviorPanel({ botId, onClose }: {
  botId: string
  onClose: () => void
}) {
  const [configuration, setConfiguration] = useState<Configuration | null>(null)
  const [editing, setEditing] = useState<ScopeTarget | null>(null)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)

  const load = useCallback(async () => {
    try {
      setError(null)
      setConfiguration(await api<Configuration>(`/bots/${botId}/behaviors`))
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : String(reason))
    }
  }, [botId])

  useEffect(() => { void load() }, [load])

  const enabled = useMemo(
    () => configuration?.behaviors.filter((behavior) => behavior.enabled) ?? [],
    [configuration],
  )

  const find = useCallback((scope: Behavior["scope"], guildId?: string, conversationId?: string) =>
    enabled.find((behavior) => behavior.scope === scope
      && (behavior.guildId ?? "") === (guildId ?? "")
      && (behavior.conversationId ?? "") === (conversationId ?? "")), [enabled])

  const defaultBehavior = find("default")
  const dmDefault = find("dm_default") ?? defaultBehavior

  const effectiveForChannel = useCallback((guildId: string, channel: GuildChannel) =>
    find("channel", guildId, channel.id)
      ?? (channel.parent_id ? find("channel", guildId, channel.parent_id) : undefined)
      ?? find("guild", guildId)
      ?? defaultBehavior, [defaultBehavior, find])

  const effectiveForDm = useCallback((conversationId: string) =>
    find("dm", undefined, conversationId) ?? dmDefault ?? defaultBehavior,
  [defaultBehavior, dmDefault, find])

  async function save(target: ScopeTarget, responseMode: ResponseMode, instructions: string) {
    setBusy(true)
    setError(null)
    try {
      await api(`/bots/${botId}/behaviors`, {
        method: "PUT",
        body: JSON.stringify({
          scope: target.scope,
          guildId: target.guildId ?? null,
          conversationId: target.conversationId ?? null,
          responseMode,
          instructions,
        }),
      })
      setEditing(null)
      setNotice(`${target.title} updated.`)
      await load()
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : String(reason))
    } finally {
      setBusy(false)
    }
  }

  async function reset(target: ScopeTarget) {
    setBusy(true)
    setError(null)
    try {
      const query = new URLSearchParams()
      if (target.guildId) query.set("guildId", target.guildId)
      if (target.conversationId) query.set("conversationId", target.conversationId)
      await api(`/bots/${botId}/behaviors/${target.scope}?${query}`, { method: "DELETE" })
      setEditing(null)
      setNotice(`${target.title} now inherits its parent behavior.`)
      await load()
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : String(reason))
    } finally {
      setBusy(false)
    }
  }

  if (!configuration) return <div className="flex h-full items-center justify-center text-sm text-text-muted">
    {error ?? "Loading Agent behavior..."}
  </div>

  return <div className="flex h-full min-h-0 flex-col bg-surface">
    <PanelHeader>
      <Button variant="ghost" size="sm" onClick={onClose}>
        <i className="ph-bold ph-arrow-left text-xs" />
        Discussions
      </Button>
      <div className="min-w-0 flex-1">
        <div className="truncate text-sm font-medium text-contrast">Discord behavior</div>
        <div className="truncate text-xs text-text-muted">{configuration.bot.agentName}</div>
      </div>
    </PanelHeader>

    <div className="min-h-0 flex-1 overflow-y-auto p-4 sm:p-6">
      <div className="mx-auto max-w-4xl space-y-6">
        <section className="flex items-center gap-3 rounded-xl border border-overlay-6 bg-overlay-2 p-4">
          {configuration.bot.agentAvatarUrl
            ? <img src={configuration.bot.agentAvatarUrl} alt="" className="size-12 rounded-full object-cover" />
            : <span className="flex size-12 items-center justify-center rounded-full bg-overlay-6 text-text-muted"><i className="ph-bold ph-user-circle text-xl" /></span>}
          <div className="min-w-0 flex-1">
            <h2 className="truncate text-base font-semibold text-contrast">{configuration.bot.agentName}</h2>
            <p className="text-xs leading-relaxed text-text-muted">
              Identity, capabilities, memory, and Skills come from the Agent. These rules add Discord-specific participation, tone, topic, and redirection.
            </p>
          </div>
        </section>

        {(error || notice || configuration.visibilityError) && <div
          className={`rounded-lg border p-3 text-xs ${error ? "border-destructive-a20 bg-destructive-a10 text-destructive" : "border-overlay-6 bg-overlay-3 text-text-muted"}`}
          role="status"
        >
          {error ?? notice ?? `Live Discord visibility is unavailable: ${configuration.visibilityError}`}
        </div>}

        <section>
          <SectionTitle title="Defaults" description="Safe fallbacks used when no more specific server, channel, forum, post, or DM override exists." />
          <div className="grid gap-3 md:grid-cols-2">
            <BehaviorCard
              title="Agent default"
              subtitle="All Discord guilds"
              behavior={defaultBehavior}
              inherited={false}
              onEdit={() => setEditing({
                scope: "default", title: "Agent default", subtitle: "All Discord guilds",
              })}
            />
            <BehaviorCard
              title="DM default"
              subtitle="All allowed direct messages"
              behavior={dmDefault}
              inherited={dmDefault?.scope !== "dm_default"}
              onEdit={() => setEditing({
                scope: "dm_default", title: "DM default", subtitle: "All allowed direct messages",
                inherited: defaultBehavior,
              })}
            />
          </div>
        </section>

        <section>
          <SectionTitle title="What this Agent sees" description="Live, permission-filtered Discord structure. Channel names and topics are context, never governing instructions." />
          {configuration.navigation.length === 0 && <EmptyState text="No visible Discord servers are currently available for this bot." />}
          <div className="space-y-4">
            {configuration.navigation.map((guild) => {
              const guildOverride = find("guild", guild.guild.id)
              const inherited = guildOverride ?? defaultBehavior
              return <div key={guild.guild.id} className="overflow-hidden rounded-xl border border-overlay-6">
                <div className="flex items-center gap-3 border-b border-overlay-6 bg-overlay-3 px-4 py-3">
                  <i className="ph-bold ph-buildings text-sm text-text-muted" />
                  <div className="min-w-0 flex-1">
                    <div className="truncate text-sm font-medium text-contrast">{guild.guild.name}</div>
                    <div className="text-xs text-text-muted">{guild.channels.length} visible places</div>
                  </div>
                  <ModeBadge mode={inherited?.responseMode ?? "direct"} inherited={!guildOverride} />
                  <Button variant="outline" size="sm" onClick={() => setEditing({
                    scope: "guild", guildId: guild.guild.id,
                    title: guild.guild.name, subtitle: "Server override",
                    inherited: defaultBehavior,
                  })}>
                    {guildOverride ? "Edit" : "Override"}
                  </Button>
                </div>
                <div className="divide-y divide-overlay-5">
                  {guild.channels.map((channel) => {
                    const exact = find("channel", guild.guild.id, channel.id)
                    const effective = effectiveForChannel(guild.guild.id, channel)
                    return <div key={channel.id} className="flex items-start gap-3 px-4 py-3">
                      <i className={`${channelIcon(channel.kind)} mt-0.5 text-sm text-text-muted`} />
                      <div className="min-w-0 flex-1">
                        <div className="flex flex-wrap items-center gap-2">
                          <span className="text-sm text-contrast">{channel.name}</span>
                          <span className="text-[10px] uppercase tracking-wide text-text-disabled">{channel.kind.replaceAll("_", " ")}</span>
                        </div>
                        {channel.topic && <p className="mt-0.5 line-clamp-2 text-xs text-text-muted">{channel.topic}</p>}
                        <div className="mt-1 flex flex-wrap gap-1">
                          <PermissionBadge allowed={channel.can_read_history} label="history" />
                          <PermissionBadge allowed={channel.can_post} label="post" />
                          {channel.kind === "thread" && <PermissionBadge allowed={channel.can_reply} label="reply" />}
                        </div>
                      </div>
                      <ModeBadge mode={effective?.responseMode ?? "direct"} inherited={!exact} />
                      <Button variant="ghost" size="sm" onClick={() => setEditing({
                        scope: "channel", guildId: guild.guild.id, conversationId: channel.id,
                        title: channel.name, subtitle: `${guild.guild.name} · ${channel.kind.replaceAll("_", " ")}`,
                        inherited: effective,
                      })}>
                        {exact ? "Edit" : "Override"}
                      </Button>
                    </div>
                  })}
                </div>
              </div>
            })}
          </div>
        </section>

        <section>
          <SectionTitle title="Direct messages" description="The DM allowlist stays in backend setup. Behavior can inherit from the DM default or be tailored per existing conversation." />
          {configuration.dms.length === 0 && <EmptyState text="No allowed DM conversation exists for this Agent yet." />}
          <div className="space-y-2">
            {configuration.dms.map((dm) => {
              const exact = find("dm", undefined, dm.conversationId)
              const effective = effectiveForDm(dm.conversationId)
              return <div key={dm.conversationId} className="flex items-center gap-3 rounded-lg border border-overlay-6 px-4 py-3">
                <i className="ph-bold ph-user text-sm text-text-muted" />
                <div className="min-w-0 flex-1">
                  <div className="truncate text-sm text-contrast">{dm.conversationName}</div>
                  <div className="text-xs text-text-muted">Existing private Discord conversation</div>
                </div>
                <ModeBadge mode={effective?.responseMode ?? "direct"} inherited={!exact} />
                <Button variant="ghost" size="sm" onClick={() => setEditing({
                  scope: "dm", conversationId: dm.conversationId,
                  title: dm.conversationName, subtitle: "Direct message override",
                  inherited: effective,
                })}>
                  {exact ? "Edit" : "Override"}
                </Button>
              </div>
            })}
          </div>
        </section>
      </div>
    </div>

    <BehaviorEditor
      target={editing}
      behavior={editing ? find(editing.scope, editing.guildId, editing.conversationId) : undefined}
      busy={busy}
      onClose={() => setEditing(null)}
      onSave={save}
      onReset={reset}
    />
  </div>
}

function BehaviorCard({ title, subtitle, behavior, inherited, onEdit }: {
  title: string
  subtitle: string
  behavior?: Behavior
  inherited: boolean
  onEdit: () => void
}) {
  return <button type="button" onClick={onEdit} className="rounded-xl border border-overlay-6 bg-overlay-2 p-4 text-left transition-colors hover:bg-overlay-4">
    <div className="flex items-start gap-3">
      <i className="ph-bold ph-sliders-horizontal mt-0.5 text-sm text-text-muted" />
      <div className="min-w-0 flex-1">
        <div className="text-sm font-medium text-contrast">{title}</div>
        <div className="text-xs text-text-muted">{subtitle}</div>
      </div>
      <ModeBadge mode={behavior?.responseMode ?? "direct"} inherited={inherited} />
    </div>
    <p className="mt-3 line-clamp-3 text-xs leading-relaxed text-text-muted">
      {behavior?.instructions || "No additional prompt. The Agent keeps its normal identity and Discord safety mandate."}
    </p>
  </button>
}

function BehaviorEditor({ target, behavior, busy, onClose, onSave, onReset }: {
  target: ScopeTarget | null
  behavior?: Behavior
  busy: boolean
  onClose: () => void
  onSave: (target: ScopeTarget, mode: ResponseMode, instructions: string) => Promise<void>
  onReset: (target: ScopeTarget) => Promise<void>
}) {
  const inherited = target?.inherited
  const [mode, setMode] = useState<ResponseMode>("direct")
  const [instructions, setInstructions] = useState("")

  useEffect(() => {
    setMode(behavior?.responseMode ?? inherited?.responseMode ?? "direct")
    setInstructions(behavior?.instructions ?? inherited?.instructions ?? "")
  }, [behavior, inherited, target])

  if (!target) return null
  const canReset = target.scope !== "default" && target.scope !== "dm_default" && behavior?.persisted
  return <Dialog open onOpenChange={(open) => { if (!open) onClose() }}>
    <DialogContent className="max-w-2xl">
      <DialogHeader>
        <DialogTitle>{target.title}</DialogTitle>
        <DialogDescription>{target.subtitle}</DialogDescription>
      </DialogHeader>
      <div className="space-y-4">
        <label className="block">
          <span className="mb-1.5 block text-sm text-text-muted">Participation</span>
          <select
            className="h-9 w-full rounded-lg border border-input bg-surface px-3 text-sm text-contrast outline-none focus-visible:border-ring focus-visible:ring-3 focus-visible:ring-ring-a50"
            value={mode}
            onChange={(event) => setMode(event.target.value as ResponseMode)}
          >
            {(Object.entries(MODE_LABELS) as Array<[ResponseMode, typeof MODE_LABELS[ResponseMode]]>).map(([value, item]) =>
              <option key={value} value={value}>{item.label}</option>)}
          </select>
          <p className="mt-1 text-xs text-text-muted">{MODE_LABELS[mode].description}</p>
        </label>
        <label className="block">
          <span className="mb-1.5 block text-sm text-text-muted">Discord prompt</span>
          <textarea
            className="min-h-48 w-full resize-y rounded-lg border border-input bg-transparent px-3 py-2 text-sm leading-relaxed text-contrast outline-none placeholder:text-muted-foreground focus-visible:border-ring focus-visible:ring-3 focus-visible:ring-ring-a50"
            value={instructions}
            maxLength={12_000}
            placeholder="Describe this room's tone, purpose, topical boundaries, and how the Agent should redirect off-topic messages."
            onChange={(event) => setInstructions(event.target.value)}
          />
          <div className="mt-1 flex justify-between text-[11px] text-text-disabled">
            <span>Additive to the Agent identity and capabilities.</span>
            <span>{instructions.length.toLocaleString()} / 12,000</span>
          </div>
        </label>
      </div>
      <div className="flex justify-between gap-2">
        <div>{canReset && <Button variant="ghost" disabled={busy} onClick={() => void onReset(target)}>Use inherited behavior</Button>}</div>
        <div className="flex gap-2">
          <Button variant="outline" disabled={busy} onClick={onClose}>Cancel</Button>
          <Button disabled={busy} onClick={() => void onSave(target, mode, instructions)}>
            {busy && <i className="ph-bold ph-spinner animate-spin text-xs" />}
            Save behavior
          </Button>
        </div>
      </div>
    </DialogContent>
  </Dialog>
}

function ModeBadge({ mode, inherited }: { mode: ResponseMode; inherited: boolean }) {
  return <Badge variant={mode === "context_only" ? "outline" : "secondary"}>
    {MODE_LABELS[mode].label}{inherited ? " · inherited" : ""}
  </Badge>
}

function PermissionBadge({ allowed, label }: { allowed: boolean; label: string }) {
  return <span className={`rounded px-1.5 py-0.5 text-[10px] ${allowed ? "bg-accent-teal-a10 text-accent-teal" : "bg-overlay-5 text-text-disabled"}`}>
    {allowed ? "can" : "no"} {label}
  </span>
}

function SectionTitle({ title, description }: { title: string; description: string }) {
  return <div className="mb-3">
    <h3 className="text-sm font-semibold text-contrast">{title}</h3>
    <p className="mt-0.5 text-xs leading-relaxed text-text-muted">{description}</p>
  </div>
}

function EmptyState({ text }: { text: string }) {
  return <div className="rounded-lg border border-dashed border-overlay-6 p-5 text-center text-xs text-text-muted">{text}</div>
}

function channelIcon(kind: string): string {
  if (kind === "forum") return "ph-bold ph-chats-circle"
  if (kind === "thread") return "ph-bold ph-chats-teardrop"
  if (kind === "announcement") return "ph-bold ph-megaphone"
  return "ph-bold ph-hash"
}

import {
  useCallback,
  useEffect,
  useMemo,
  useState,
  type ComponentProps,
  type ReactNode,
} from "react"
import {
  Badge,
  Button,
  Dialog,
  DialogContent,
  DialogDescription,
  DialogHeader,
  DialogTitle,
  EntityCard,
  Input,
  ProtectedValueField,
  ScrollArea,
  SectionHeader,
  SettingRow,
  Switch,
  useEntityCardPresentation,
  type EntityCardEntity,
  type ProtectedValueProtection,
  type ProtectedValueVerification,
} from "@redbamboo/ui"

const SETTINGS_BASE = "/api/extensions/leaf-discord"
const APP_BASE = "/api/apps/leaf-discord"

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

type BotConfig = {
  botId: string
  applicationId: string
  interactionPublicKey: string
  hasBotToken: boolean
  enabled: boolean
  agentId: string
  sentinelAgentId: string
  sessionQualityMode: string
  sentinelQualityMode: string
  audienceProfile: string
  dmAllowlist: string[]
  clearAllowlist: string[]
  maxAttachmentBytes: number
  presenceContextMode: "off" | "active" | "roster"
  presenceExtensionFeedEnabled: boolean
  showToolActivity?: boolean
  agentName?: string
  agentAvatarUrl?: string
}

type BotItem = { config: BotConfig; status: BotStatus }

type Field = {
  key: string
  type: "boolean" | "number" | "string" | "string_list" | "secret" | "action" | "select" | "entity_ref"
  value?: unknown
  configured?: boolean
  protection?: ProtectedValueProtection
  verification?: ProtectedValueVerification
  description?: string
  targetType?: string
  options?: FieldOption[]
}

type FieldOption = {
  value: string
  label: string
  description?: string
  entity?: EntityCardEntity
}

type Section = { id: string; label: string; fields: Field[] }
type Descriptor = { status: BotStatus; sections: Section[] }

async function request<T>(base: string, path: string, init?: RequestInit): Promise<T> {
  const response = await fetch(`${base}${path}`, {
    credentials: "same-origin",
    headers: { "Content-Type": "application/json", ...init?.headers },
    ...init,
  })
  const body = await response.json().catch(() => ({}))
  if (!response.ok) throw new Error(body.message ?? body.error ?? `Request failed (${response.status})`)
  return body as T
}

function ids(value: string): string[] {
  return value.split(/[\s,]+/).map((item) => item.trim()).filter(Boolean)
}

export function DiscordSettingsPanel() {
  const [descriptor, setDescriptor] = useState<Descriptor | null>(null)
  const [bots, setBots] = useState<BotItem[]>([])
  const [selectedBotId, setSelectedBotId] = useState<string | null>(null)
  const [adding, setAdding] = useState(false)
  const [notice, setNotice] = useState("")
  const [error, setError] = useState("")
  const [busy, setBusy] = useState(false)

  const reload = useCallback(async () => {
    try {
      const [nextDescriptor, result] = await Promise.all([
        request<Descriptor>(SETTINGS_BASE, "/settings"),
        request<{ items: BotItem[] }>(APP_BASE, "/bots"),
      ])
      setDescriptor(nextDescriptor)
      setBots(result.items)
      setSelectedBotId((current) =>
        current && result.items.some((item) => item.config.botId === current)
          ? current
          : result.items[0]?.config.botId ?? null)
      setError("")
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : "Could not load Discord settings")
    }
  }, [])

  useEffect(() => { void reload() }, [reload])

  const selected = bots.find((item) => item.config.botId === selectedBotId) ?? null
  const optionField = (key: string) => descriptor?.sections
    .flatMap((section) => section.fields)
    .find((field) => field.key === key)

  const payload = (config: BotConfig, key?: string, value?: unknown) => ({
    applicationId: config.applicationId,
    interactionPublicKey: key === "interaction_public_key" ? value : config.interactionPublicKey,
    botToken: key === "bot_token" ? value : null,
    clearBotToken: false,
    enabled: key === "enabled" ? value : config.enabled,
    agentId: key === "default_agent" ? value : config.agentId,
    sentinelAgentId: key === "default_agent" ? value : config.sentinelAgentId,
    sessionQualityMode: key === "session_quality_mode" ? value : config.sessionQualityMode,
    sentinelQualityMode: key === "sentinel_quality_mode" ? value : config.sentinelQualityMode,
    dmAllowlist: key === "dm_allowlist" ? value : config.dmAllowlist,
    clearAllowlist: key === "clear_allowlist" ? value : config.clearAllowlist,
    maxAttachmentBytes: key === "max_attachment_bytes" ? value : config.maxAttachmentBytes,
    presenceContextMode: key === "presence_context_mode" ? value : config.presenceContextMode,
    presenceExtensionFeedEnabled: key === "presence_extension_feed_enabled"
      ? value : config.presenceExtensionFeedEnabled,
    showToolActivity: key === "show_tool_activity"
      ? value : config.showToolActivity ?? true,
  })

  const save = async (_section: string, key: string, value: unknown) => {
    if (!selected) return
    setNotice("")
    setError("")
    try {
      await request(APP_BASE, `/bots/${selected.config.botId}`, {
        method: "PUT",
        body: JSON.stringify(payload(selected.config, key, value)),
      })
      setNotice("Saved. This bot transport was reconciled.")
      await reload()
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : "Could not save Discord settings")
      throw cause
    }
  }

  const restart = async () => {
    if (!selected) return
    setBusy(true)
    setNotice("")
    setError("")
    try {
      await request(APP_BASE, `/bots/${selected.config.botId}/restart`, { method: "POST" })
      setNotice(`${selected.config.agentName ?? selected.config.agentId} transport restarted.`)
      await reload()
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : "Could not restart Discord")
    } finally {
      setBusy(false)
    }
  }

  const status = selected?.status
  const agentField = optionField("default_agent")
  const sessionField = optionField("session_quality_mode")
  const sentinelField = optionField("sentinel_quality_mode")
  const presenceField = optionField("presence_context_mode")
  return <div>
    <div className="flex items-center justify-between gap-3">
      <SectionHeader>Discord bots</SectionHeader>
      <Button size="sm" onClick={() => setAdding(true)}>
        <i className="ph-bold ph-plus text-xs" />
        Add bot
      </Button>
    </div>
    <p className="mb-3 text-xs leading-relaxed text-text-muted">
      Backend setup only: credentials, runtime, compute, access, and Discord intents. Configure how each Agent speaks, listens, and handles individual servers or channels from the <a className="text-primary hover:underline" href="/apps/nova/discord">Discord section in Nova</a>.
    </p>

    <div className="mb-4 grid gap-2">
      {bots.map((item) => <ReferenceCard
        key={item.config.botId}
        option={{
          value: item.config.botId,
          label: item.config.agentName ?? item.config.agentId,
          description: `${item.status.state} · application ${item.config.applicationId}`,
          entity: agentField?.options?.find(
            (option) => option.value === item.config.agentId)?.entity ?? {
            id: item.config.agentId,
            typeSlug: "agent",
            name: item.config.agentName ?? item.config.agentId,
          },
        }}
        selected={item.config.botId === selectedBotId}
        action={{
          kind: "button",
          ariaLabel: `Configure ${item.config.agentName ?? item.config.agentId}`,
          onActivate: () => setSelectedBotId(item.config.botId),
        }}
        trailing={<Badge variant="secondary">{item.status.state}</Badge>}
      />)}
      {bots.length === 0 && (
        <button
          type="button"
          className="rounded-md border border-dashed border-overlay-6 p-5 text-sm text-text-muted hover:bg-overlay-4"
          onClick={() => setAdding(true)}
        >
          Add the first Agent bot
        </button>
      )}
    </div>

    <div className="mb-3 grid grid-cols-2 gap-2 sm:grid-cols-4" aria-label="Discord runtime status">
      <Metric label="Bridge" value={status?.state ?? "Checking"} destructive={status?.state === "error"} />
      <Metric label="Bot" value={status?.botUser ?? (status?.configured ? "Configured" : "Needs token")} />
      <Metric label="Guilds" value={status ? String(status.guilds) : "..."} />
      <Metric label="Secret" value={status?.configured ? "Encrypted" : "Missing"} />
    </div>

    <div className="mb-3">
      <Button variant="outline" size="sm" disabled={busy || !selected} onClick={() => void restart()}>
        <i className="ph-bold ph-arrows-clockwise text-xs" />
        Restart Discord transport
      </Button>
    </div>

    <p aria-live="polite" className={`mb-2 text-xs ${error ? "text-accent-red" : "text-accent-teal"}`}>
      {error || notice}
    </p>
    {(status?.lastError || status?.profileWarning) && (
      <p className="mb-3 rounded-lg border border-destructive-a20 bg-destructive-a10 p-2 text-xs text-destructive">
        {status.lastError ?? status.profileWarning}
      </p>
    )}

    {selected && <>
      <section>
        <SectionHeader>Connection</SectionHeader>
        <SettingRow label="Application ID" hint="Immutable Discord application identity. Add another bot to use a different application.">
          <code className="text-xs text-contrast">{selected.config.applicationId}</code>
        </SettingRow>
        <FieldControl section="connection" field={{
          key: "interaction_public_key", type: "string",
          value: selected.config.interactionPublicKey,
          description: "Discord interaction verification public key.",
        }} onSave={save} />
        <FieldControl section="connection" field={{
          key: "bot_token", type: "secret",
          configured: selected.config.hasBotToken,
          protection: "encrypted",
          verification: status?.state === "connected" ? "verified" : "unverified",
          description: "Credential for this Agent's dedicated Discord bot.",
        }} onSave={save} />
        <FieldControl section="connection" field={{
          key: "enabled", type: "boolean", value: selected.config.enabled,
          description: "Connect this bot when RedLeaf is running.",
        }} onSave={save} />
      </section>
      <section>
        <SectionHeader>Agent and compute</SectionHeader>
        {agentField && <FieldControl section="access" field={{
          ...agentField, value: selected.config.agentId,
          description: "Agent represented by this dedicated Discord bot.",
        }} onSave={save} />}
        {sessionField && <FieldControl section="access" field={{
          ...sessionField, value: selected.config.sessionQualityMode,
        }} onSave={save} />}
        {sentinelField && <FieldControl section="access" field={{
          ...sentinelField, value: selected.config.sentinelQualityMode,
        }} onSave={save} />}
      </section>
      <section>
        <SectionHeader>Access and attachments</SectionHeader>
        <FieldControl section="access" field={{
          key: "dm_allowlist", type: "string_list", value: selected.config.dmAllowlist,
          description: "Discord user IDs allowed to talk to this Agent in direct messages.",
        }} onSave={save} />
        <FieldControl section="access" field={{
          key: "clear_allowlist", type: "string_list", value: selected.config.clearAllowlist,
          description: "Discord user IDs allowed to clear this bot's shared conversation context.",
        }} onSave={save} />
        <FieldControl section="attachments" field={{
          key: "max_attachment_bytes", type: "number", value: selected.config.maxAttachmentBytes,
          description: "Maximum accepted attachment size in bytes (1 to 25 MiB).",
        }} onSave={save} />
        <FieldControl section="replies" field={{
          key: "show_tool_activity", type: "boolean",
          value: selected.config.showToolActivity ?? true,
          description: "Show tool-call squares in the progress message while the Agent works.",
        }} onSave={save} />
      </section>
      <section>
        <SectionHeader>Guild context</SectionHeader>
        <FieldControl section="presence" field={{
          ...(presenceField ?? {
            key: "presence_context_mode",
            type: "select",
            options: [
              { value: "off", label: "Off" },
              { value: "active", label: "Active members" },
              { value: "roster", label: "Full roster" },
            ],
          }),
          value: selected.config.presenceContextMode,
          description: "Adds a bounded, untrusted status/activity/voice snapshot to new guild turns. Active requires Discord Presence Intent; Full roster also requires Server Members Intent. Discord cannot report who is reading a text channel.",
        }} onSave={save} />
        <FieldControl section="presence" field={{
          key: "presence_extension_feed_enabled",
          type: "boolean",
          value: selected.config.presenceExtensionFeedEnabled,
          description: "Privately feeds your linked Discord game, music, voice channel, and bounded companion names into Presence. Presence then updates Nova context and LIVE. Requires a non-Off guild context mode.",
        }} onSave={save} />
      </section>
    </>}

    <AddBotDialog
      open={adding}
      onOpenChange={setAdding}
      agentField={agentField}
      sessionField={sessionField}
      sentinelField={sentinelField}
      onCreated={async () => {
        setNotice("Discord bot added.")
        await reload()
      }}
      onError={(message) => setError(message)}
    />
  </div>
}

function AddBotDialog({
  open,
  onOpenChange,
  agentField,
  sessionField,
  sentinelField,
  onCreated,
  onError,
}: {
  open: boolean
  onOpenChange: (open: boolean) => void
  agentField?: Field
  sessionField?: Field
  sentinelField?: Field
  onCreated: () => Promise<void>
  onError: (message: string) => void
}) {
  const [applicationId, setApplicationId] = useState("")
  const [publicKey, setPublicKey] = useState("")
  const [token, setToken] = useState("")
  const [agentId, setAgentId] = useState("")
  const [sessionMode, setSessionMode] = useState("")
  const [sentinelMode, setSentinelMode] = useState("")
  const [saving, setSaving] = useState(false)

  useEffect(() => {
    if (!open) return
    setApplicationId("")
    setPublicKey("")
    setToken("")
    setAgentId(agentField?.options?.[0]?.value ?? "")
    setSessionMode(sessionField?.value == null
      ? sessionField?.options?.[0]?.value ?? ""
      : String(sessionField.value))
    setSentinelMode(sentinelField?.value == null
      ? sentinelField?.options?.[0]?.value ?? ""
      : String(sentinelField.value))
  }, [agentField, open, sentinelField, sessionField])

  const create = async () => {
    setSaving(true)
    onError("")
    try {
      await request(APP_BASE, "/bots", {
        method: "POST",
        body: JSON.stringify({
          applicationId,
          interactionPublicKey: publicKey,
          botToken: token || null,
          clearBotToken: false,
          enabled: false,
          agentId,
          sentinelAgentId: agentId,
          sessionQualityMode: sessionMode,
          sentinelQualityMode: sentinelMode,
          dmAllowlist: [],
          clearAllowlist: [],
          maxAttachmentBytes: 10 * 1024 * 1024,
          presenceContextMode: "off",
          presenceExtensionFeedEnabled: false,
          showToolActivity: true,
        }),
      })
      onOpenChange(false)
      await onCreated()
    } catch (cause) {
      onError(cause instanceof Error ? cause.message : "Could not add Discord bot")
    } finally {
      setSaving(false)
    }
  }

  return <Dialog open={open} onOpenChange={onOpenChange}>
    <DialogContent className="max-w-xl">
      <DialogHeader>
        <DialogTitle>Add an Agent bot</DialogTitle>
        <DialogDescription>
          Create a dedicated Discord application first, then bind its credentials to one Leaf Agent.
          The bot stays disabled until you explicitly enable it.
        </DialogDescription>
      </DialogHeader>
      <div className="space-y-3">
        <SettingRow label="Application ID">
          <Input value={applicationId} onChange={(event) => setApplicationId(event.target.value)} />
        </SettingRow>
        <SettingRow label="Interaction public key">
          <Input value={publicKey} onChange={(event) => setPublicKey(event.target.value)} />
        </SettingRow>
        <SettingRow label="Bot token" hint="Stored as an encrypted RedLeaf Entity secret.">
          <Input type="password" value={token} onChange={(event) => setToken(event.target.value)} />
        </SettingRow>
        {agentField && <EntityReferenceControl
          label="Agent"
          field={{ ...agentField, value: agentId }}
          onSave={async (value) => setAgentId(value)}
        />}
        {sessionField && <EntityReferenceControl
          label="Session quality mode"
          field={{ ...sessionField, value: sessionMode }}
          onSave={async (value) => setSessionMode(value)}
        />}
        {sentinelField && <EntityReferenceControl
          label="Sentinel quality mode"
          field={{ ...sentinelField, value: sentinelMode }}
          onSave={async (value) => setSentinelMode(value)}
        />}
      </div>
      <div className="flex justify-end gap-2">
        <Button variant="outline" onClick={() => onOpenChange(false)}>Cancel</Button>
        <Button
          disabled={saving || !applicationId || publicKey.length !== 64 || !agentId || !sessionMode || !sentinelMode}
          onClick={() => void create()}
        >
          {saving && <i className="ph-bold ph-spinner animate-spin text-xs" />}
          Add bot
        </Button>
      </div>
    </DialogContent>
  </Dialog>
}

function Metric({ label, value, destructive = false }: {
  label: string
  value: string
  destructive?: boolean
}) {
  return <div className="rounded-lg border border-overlay-6 bg-overlay-3 p-2">
    <span className="block text-[10px] uppercase tracking-wide text-text-muted">{label}</span>
    <strong className={`block truncate text-xs font-medium capitalize ${destructive ? "text-destructive" : "text-contrast"}`}>
      {value}
    </strong>
  </div>
}

function FieldControl({
  section,
  field,
  onSave,
}: {
  section: string
  field: Field
  onSave: (section: string, key: string, value: unknown) => Promise<void>
}) {
  const initial = Array.isArray(field.value)
    ? field.value.join("\n")
    : field.value == null ? "" : String(field.value)
  const [draft, setDraft] = useState(initial)

  useEffect(() => { setDraft(initial) }, [initial])

  const label = field.key.split("_")
    .map((word) => word[0]?.toUpperCase() + word.slice(1))
    .join(" ")

  if (field.type === "boolean") {
    return <SettingRow label={label} hint={field.description}>
      <Switch
        checked={field.value === true}
        onCheckedChange={(value) => void onSave(section, field.key, value)}
      />
    </SettingRow>
  }

  if (field.type === "secret") {
    return <SettingRow label={label} hint={field.description}>
      <ProtectedValueField
        className="w-full"
        kind="token"
        status={{
          configured: field.configured === true,
          protection: field.protection ?? "unknown",
          verification: field.verification ?? "unverified",
        }}
        placeholder="Paste a Discord bot token"
        allowClear={false}
        onReplace={(value) => onSave(section, field.key, value)}
      />
    </SettingRow>
  }

  if (field.type === "string_list") {
    return <SettingRow label={label} hint={field.description}>
      <div className="flex w-full items-end gap-2">
        <textarea
          className="min-h-20 flex-1 resize-y rounded-lg border border-input bg-transparent px-2.5 py-2 text-xs outline-none placeholder:text-muted-foreground focus-visible:border-ring focus-visible:ring-3 focus-visible:ring-ring-a50"
          value={draft}
          placeholder="One Discord user ID per line"
          onChange={(event) => setDraft(event.target.value)}
        />
        <Button variant="outline" size="sm" onClick={() => void onSave(section, field.key, ids(draft))}>
          Save
        </Button>
      </div>
    </SettingRow>
  }

  if (field.type === "entity_ref") {
    return <div
      data-slot="setting-row"
      data-setting-path={`${section}.${field.key}`}
      className="py-2.5"
    >
      <span className="text-sm text-text-muted">{label}</span>
      {field.description && (
        <p className="mt-1 text-xs leading-relaxed text-muted-a60">{field.description}</p>
      )}
      <div className="mt-2">
      <EntityReferenceControl
        label={label}
        field={field}
        onSave={(value) => onSave(section, field.key, value)}
      />
      </div>
    </div>
  }

  if (field.type === "select") {
    return <SettingRow label={label} hint={field.description}>
      <select
        className="h-8 w-full rounded-lg border border-input bg-surface px-2.5 text-xs text-contrast outline-none focus-visible:border-ring focus-visible:ring-3 focus-visible:ring-ring-a50"
        value={draft}
        onChange={(event) => {
          setDraft(event.target.value)
          void onSave(section, field.key, event.target.value)
        }}
      >
        {(field.options ?? []).map((option) => (
          <option key={option.value} value={option.value} title={option.description}>
            {option.label}
          </option>
        ))}
      </select>
    </SettingRow>
  }

  return <SettingRow label={label} hint={field.description}>
    <div className="flex w-full items-center gap-2">
      <Input
        type={field.type === "number" ? "number" : "text"}
        className="h-7 flex-1 text-xs"
        value={draft}
        onChange={(event) => setDraft(event.target.value)}
        onKeyDown={(event) => { if (event.key === "Enter") event.currentTarget.blur() }}
      />
      <Button
        variant="outline"
        size="sm"
        disabled={draft === ""}
        onClick={() => void onSave(
          section,
          field.key,
          field.type === "number" ? Number(draft) : draft,
        )}
      >
        Save
      </Button>
    </div>
  </SettingRow>
}

function EntityReferenceControl({
  label,
  field,
  onSave,
}: {
  label: string
  field: Field
  onSave: (value: string) => Promise<void>
}) {
  const [open, setOpen] = useState(false)
  const [search, setSearch] = useState("")
  const [saving, setSaving] = useState<string | null>(null)
  const currentValue = field.value == null ? "" : String(field.value)
  const options = field.options ?? []
  const current = options.find((option) => option.value === currentValue)
  const filtered = useMemo(() => {
    const query = search.trim().toLowerCase()
    if (!query) return options
    return options.filter((option) =>
      option.label.toLowerCase().includes(query)
      || option.description?.toLowerCase().includes(query))
  }, [options, search])

  const select = async (option: FieldOption) => {
    if (option.value === currentValue) {
      setOpen(false)
      return
    }
    setSaving(option.value)
    try {
      await onSave(option.value)
      setOpen(false)
      setSearch("")
    } catch {
      // The parent owns the visible error message.
    } finally {
      setSaving(null)
    }
  }

  return <div className="w-full">
    {current ? (
      <ReferenceCard
        option={current}
        actions={(
          <Button
            variant="outline"
            size="sm"
            aria-label={`Change ${label}`}
            onClick={() => setOpen(true)}
          >
            Change
          </Button>
        )}
      />
    ) : (
      <button
        type="button"
        className="flex w-full items-center gap-3 rounded-md border border-dashed border-overlay-6 px-3 py-2.5 text-left text-sm text-text-muted transition-colors hover:border-primary/40 hover:bg-overlay-4"
        onClick={() => setOpen(true)}
      >
        <i className="ph-bold ph-link text-xs" aria-hidden="true" />
        Select {label.toLowerCase()}
      </button>
    )}

    <Dialog open={open} onOpenChange={(next) => {
      setOpen(next)
      if (!next) setSearch("")
    }}>
      <DialogContent className="max-w-lg">
        <DialogHeader>
          <DialogTitle>Change {label}</DialogTitle>
          <DialogDescription>
            Select a {humanizeType(field.targetType)} entity.
          </DialogDescription>
        </DialogHeader>
        <Input
          value={search}
          onChange={(event) => setSearch(event.target.value)}
          placeholder={`Search ${humanizeType(field.targetType)} entities`}
          aria-label={`Search ${label}`}
        />
        <ScrollArea className="max-h-[420px] -mx-6 px-6">
          <div className="space-y-1 py-1" aria-label={`${label} entities`}>
            {filtered.map((option) => (
              <ReferenceCard
                key={option.value}
                option={option}
                selected={option.value === currentValue}
                disabled={saving !== null}
                action={{
                  kind: "button",
                  ariaLabel: `Select ${option.label}`,
                  onActivate: () => void select(option),
                }}
                trailing={saving === option.value
                  ? <i className="ph-bold ph-spinner animate-spin text-sm text-text-muted" aria-label="Saving" />
                  : option.value === currentValue
                    ? <Badge variant="secondary">Current</Badge>
                    : undefined}
              />
            ))}
            {filtered.length === 0 && (
              <p className="py-8 text-center text-sm text-text-muted">No matching entities</p>
            )}
          </div>
        </ScrollArea>
      </DialogContent>
    </Dialog>
  </div>
}

function ReferenceCard({
  option,
  action,
  selected,
  disabled,
  trailing,
  actions,
}: {
  option: FieldOption
  action?: ComponentProps<typeof EntityCard>["action"]
  selected?: boolean
  disabled?: boolean
  trailing?: ReactNode
  actions?: ReactNode
}) {
  const fallback = option.entity ?? {
    id: option.value,
    typeSlug: "entity",
    name: option.label,
  }
  const presentation = useEntityCardPresentation(fallback)
  const entity = presentation?.entity ?? fallback
  const inspectAction = option.entity ? {
    kind: "inspect" as const,
    href: `/database/entities/${encodeURIComponent(option.entity.typeSlug)}/${encodeURIComponent(option.entity.id)}`,
    ariaLabel: `Inspect ${option.label}`,
  } : undefined

  return <EntityCard
    entity={entity}
    visual={presentation?.visual ?? {
      icon: entity.typeSlug === "agent" ? "ph-bold ph-user-circle" : "ph-bold ph-gauge",
    }}
    subtitle={option.description ?? presentation?.subtitle ?? humanizeType(entity.typeSlug)}
    variant="outlined"
    action={action ?? inspectAction}
    selected={selected}
    disabled={disabled}
    trailing={trailing}
    actions={actions}
  />
}

function humanizeType(typeSlug?: string): string {
  return (typeSlug || "entity").replaceAll("-", " ")
}

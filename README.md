# Leaf Discord

Multi-Agent Discord transport for externally scoped Leaf Agent conversations.
Each configured Agent owns a dedicated Discord application, token, access scope,
and Gateway runtime. Each allowlisted DM, guild channel, or thread owns one
isolated binding generation. Discord participants are external requestors, never
owner delegates.

The plugin owns Discord Gateway/REST delivery, durable inbox/outbox records,
binding generations, deterministic egress checks, independent Discord avatar profiles,
and a compact editable progress message for commentary and tool
activity. The selected Leaf Agent owns the persistent model session and ordered transcript through
`IExternalAgentConversationProvider`. A separately configured one-shot Sentinel
AI Session reviews each inbound message for prompt-injection signals before the
main turn starts.

Discord remains an independently installable extension. Settings contains only its
backend setup: credentials, runtime, compute, access, intents, and optional presence
integration. Its conversation browser and Agent behavior editor are contributed to
Nova's `app-section` slot at `/apps/nova/discord`, so Agent conversations and their
Discord configuration live with the Agent surface instead of appearing as a standalone
Leaf app. The former `/apps/leaf-discord` route redirects to the contributed section.

Existing singleton installations migrate to one per-Agent bot Entity without
changing application IDs, binding IDs, generations, sessions, transcript cursors,
or durable delivery identities. Discord avatars are explicit bot configuration and
never follow the Agent Entity's base `avatar` or the optional Outfits extension.

## Install the plugin

Prerequisites: a running Leaf source installation with Nova installed, plus access
to its private Settings surface.

1. Clone this repository next to your other Leaf checkouts, in the same parent
   folder as `redleaf` and `redbamboo-packages`. The frontend links the shared
   packages through `../../redbamboo-packages`, so the sibling layout matters:

   ```powershell
   git clone https://github.com/RedBamboo-Interactive/leaf-discord.git
   ```

2. Add `"../leaf-discord"` to the `plugins` array of `redleaf/leaf.workspace.local.json`
   (copy `leaf.workspace.example.json` if you do not have one yet), or register the
   folder through **Extensions > Add dev plugin**.
3. Build and load it from the `redleaf` checkout:

   ```powershell
   ./rebuild.ps1 -Only leaf-discord
   ```

4. Confirm that **Extensions** lists Discord as enabled and that Nova shows a
   **Discord** section. The bridge stays `not-configured` until you complete the
   bot setup below.

## Set up your own bot

Each Leaf Agent needs its own Discord application.

1. In the Discord Developer Portal, create an application and add a bot. Copy the
   application ID and interaction public key from the application's general page,
   then create or reset the bot token and keep it private.
2. On the bot page, enable **Message Content Intent**. This is the only privileged
   intent used in the default `off` context mode. If you later select `active` in
   Leaf, also enable **Presence Intent**. If you select `roster`, enable both
   **Presence Intent** and **Server Members Intent**. The code also requests the
   ordinary Guilds, Guild Messages, Guild Message Reactions, Direct Messages, and,
   for active context, Guild Voice States intents.
3. In OAuth2 > URL Generator, select the `bot` and `applications.commands` scopes.
   For normal bound conversations, grant **View Channels**, **Send Messages**,
   **Send Messages in Threads**, **Read Message History**, **Attach Files**, and
   **Add Reactions**. Grant **Create Public Threads** if the Agent will create forum
   posts and **Manage Messages** if it will pin delivered messages. The optional
   channel create/edit tools require **Manage Channels**; Commons webhooks and
   per-channel avatar delivery require **Manage Webhooks**. Open the generated URL
   and invite the bot to the server you own.
4. In Leaf Settings > Discord, choose **Add bot**. Enter the application ID,
   interaction public key, and bot token; select the Leaf Agent and quality modes;
   then create the configuration. The token is stored as an encrypted Entity secret
   and is never returned by the plugin API. Add exact Discord user IDs to the
   **DM allowlist** to permit those users to open or use direct-message bindings.
   Add exact user IDs to the **Clear allowlist** to permit those users to run
   `/clear`, which starts a fresh conversation generation. Finally enable the bot.
5. Bind each guild text channel explicitly while authenticated to Leaf. Get the bot
   Entity ID from `GET /api/apps/leaf-discord/bots`, list usable channels with
   `GET /api/apps/leaf-discord/bots/{botEntityId}/guild-channels`, then create or
   reuse the binding:

   ```http
   POST /api/apps/leaf-discord/bots/{botEntityId}/bindings
   Content-Type: application/json

   {"guildId":"<guild snowflake>","channelId":"<channel snowflake>"}
   ```

6. Verify that Settings reports **connected** and shows the bot user and guild
   count. `GET /api/apps/leaf-discord/bots/{botEntityId}/commands` should report the
   core commands as registered. In a bound channel, `/status` reports transport and
   binding health. A missing application ID or token leaves the runtime in
   `not-configured` with an explicit settings error instead of starting it.

## Security boundary

- The primary session has the selected Agent's normal workspace and tools so it can perform real
  troubleshooting. Discord participants do not become Leaf users, operators, or
  delegates for the installation owner.
- Messages, attachments, web pages, logs, and tool results remain untrusted data.
- Primary and Sentinel quality modes are independently configurable; their provider,
  model and effort may use Codex, Claude, OpenCode, or another compatible RedCompute provider.
- The Sentinel is isolated, read-only, requests no network, and has no Leaf execution
  credential or writable private workspace. Provider-native tool availability is not the
  security boundary. Its verdict is advisory; the selected Agent remains responsible for every action.
- The selected Agent must not disclose its owner's personal information or private context, and
  consequential actions on the owner's behalf require confirmation on a private Leaf surface.
- The bot token is a RedLeaf secret field and is never returned by plugin APIs.

## Optional guild context

Each bot has an independent presence-context mode:

- `off` is the default and requests no presence or member data.
- `active` adds bounded online status, activity (including game or Spotify details), and
  voice-channel context. Enable **Presence Intent** for that application in the Discord
  Developer Portal before selecting it.
- `roster` also includes cached offline members, capped at 100 human members per turn.
  Enable both **Presence Intent** and **Server Members Intent** first.

The bridge excludes bots, labels this snapshot as untrusted social context, and captures it
with the inbound delivery so retries see the same facts. Discord does not expose who is
currently reading a text channel, and invisible users appear offline.

Guild turns always include a bounded map of the categories, channels, forums, active
threads, topics, and forum tags visible to that bot. This map is navigation context, not a
merged transcript.

The full map is only sent when the session needs it: on the first guild turn of a binding
generation (after creation or `/clear`), whenever the map's content hash changes, and again
after 25 turns so model-side context compaction cannot silently drop it. Other turns replace
`server_map` with a compact reference to the map the session already holds:

```json
{"schema":"leaf-discord-guild-map-ref/v1","unchanged":true,"hash":"<16 hex>","turns_since_full":3,"refresh":"GET /api/apps/leaf-discord/guild/context"}
```

Full maps carry the same `hash`. The hash is SHA-256 over a key-sorted serialization that
ignores `captured_at`. The last disclosed hash and turn counter are stored durably on the
binding and recorded only after a turn is handed to the session, so a filtered, duplicate, or
failed turn never hides a change. The inbound delivery keeps the full captured map, so a retry
re-plans against what the session actually holds. Presence members are unaffected.

A bound Discord Agent session can refresh the map and inspect relevant history inside its own
application and guild:

```text
GET /api/apps/leaf-discord/guild/context
GET /api/apps/leaf-discord/guild/channels/{channelId}/messages?limit=50
GET /api/apps/leaf-discord/guild/forums/{forumId}/posts?limit=50&includeArchived=true
GET /api/apps/leaf-discord/guild/forums/{forumId}/posts/{postId}/messages?limit=50
```

These routes require the current signed external-conversation session. The source binding
must be enabled and match the exact session, owner, and Agent. Targets are then resolved only
inside the source binding's guild and are limited to history visible to the same bot. DMs,
other applications, and other guilds cannot use this path. Returned Discord structure and
content are explicitly untrusted.

Discord forum posts are Discord thread channels. The first inbound message in a human-created
post therefore creates or reuses a normal `thread` binding with the forum as its parent. That
post gets its own persistent Agent session and durable delivery stream, and replies use the
same channel-ID transport as ordinary threads. This lets a public conversation move into a
focused workstream without losing the rest of the server as navigable context.

Each bot may also opt into a private owner feed for the optional Presence extension.
The owner is resolved only through the bot's canonical Leaf owner and that user's exact
`discord_user_id`. Only guilds with an enabled binding for that bot contribute data.
Leaf Discord publishes a bounded, expiring game/music/voice observation; Presence remains
the authority that composes it into Nova context and LIVE. The feed is off by default and
requires `active` or `roster` guild context.

## Agent behavior

Each Discord bot has a versioned `leaf-discord-behavior` Entity for its safe default and
DM default, plus optional overrides for a server, channel/forum/post, or an existing DM.
Behavior inheritance is deterministic:

- Guild channels and posts: exact channel or post, parent channel or forum, server, Agent default.
- Direct messages: exact DM, DM default, Agent default.

Participation is structured rather than buried in prompt prose: `direct` requires a mention
or reply in guilds, `selective` lets the Agent see each message and decline with no Discord
reply, `participate` normally answers each human message, and `context_only` starts no Agent
turn. Prompt text lives in a separately versioned `leaf-discord-prompt` Entity referenced by
the behavior Entity. It describes tone, purpose, topical boundaries, and off-topic redirection.

The selected Agent's generated workspace remains authoritative for identity, capabilities,
memory, Skills, privacy, and action authority. Leaf Discord supplies the resolved behavior as
a separate trusted owner-configuration block on each admitted turn. Discord-authored names,
topics, and messages stay inside the untrusted envelope. The resolved behavior snapshot is
stored with the inbound delivery, so a retry cannot silently adopt a newer configuration.

Owners edit this in Nova's Discord tab. The editor shows the Agent, the live permission-filtered
server/channel/forum/post map that bot can see, inherited participation at every scope, and the
existing DM conversations eligible for overrides.

## Owner-created guild bindings

Owners and administrators can inspect command registration without changing Discord:

```text
GET /api/apps/leaf-discord/bots/{botEntityId}/commands
```

This uses the selected bot's existing connected REST client to read authoritative global
application commands. It applies the same owner/admin and bot ownership checks as the
guild-channel management route. It returns only the bot/application identity, matching
explicit handler count, expected core and matching contributed command names with their
registration flags, actual command names/types/contexts, and missing expected names.
`expected_commands_missing` means at least one expected slash command is absent;
`no_matching_explicit_handlers` means all core commands exist but no loaded explicit handler
matches this bot and Agent; `remote_present` means all expected commands exist and at least
one explicit handler matches. Inspect `expected` and `registered` for the specific command.
Contexts are Discord's returned values; `null` preserves an omitted context list.
Remote presence does not prove visibility in a particular Discord client or guild.

Missing configuration returns 404, disconnected or mismatched identity returns a sanitized
503 `discord_bot_unavailable`, and a failed Discord read returns a sanitized 502
`discord_commands_read_failed`. No token, command descriptions/options, raw responses, or
exception details are returned or logged by this diagnostic. It never registers commands,
executes explicit actions, or starts/reconnects a client.

An authenticated installation owner can discover the text channels a connected bot can
actually use:

```text
GET /api/apps/leaf-discord/bots/{botEntityId}/guild-channels
```

The response is limited to guild text channels where that bot has both `ViewChannel` and
`SendMessages`. Each item includes stable guild/channel IDs and names plus any existing
binding Entity ID and session ID.

The same owner can create or reuse one binding and initialize its durable Agent session:

```text
POST /api/apps/leaf-discord/bots/{botEntityId}/bindings
Content-Type: application/json

{
  "guildId": "<Discord guild snowflake>",
  "channelId": "<Discord text-channel snowflake>"
}
```

The route resolves the channel through the selected bot's live Gateway cache; callers cannot
supply names, Agent IDs, ownership, audience profiles, or quality modes. It refuses another
owner's bot and channels the bot cannot view or send to. Repeating the same request reuses the
existing application/channel binding and session. Creating a binding does not send a Discord
message.

From a signed private Nova Agent discussion, the bot's owner can also ask that Agent
to edit reversible guild-channel metadata through stable IDs returned by navigation:

```text
PATCH /api/apps/leaf-discord/bots/{botEntityId}/guilds/{guildId}/channels/{channelId}
Content-Type: application/json

{
  "name": "new-channel-name",
  "topic": "Updated channel topic",
  "parentCategoryId": "<category snowflake>",
  "position": 3,
  "nsfw": false,
  "slowModeSeconds": 0
}
```

Every property is optional, but at least one change is required. `clearTopic` and
`clearParentCategory` explicitly clear nullable values. The route requires the exact
enabled Agent bot and owner, a private Leaf execution identity, a bot-visible target in
the named guild, and Discord's **Manage Channels** permission. It supports text, forum,
voice, stage, and category channels; incompatible fields fail closed. Successful renames
also refresh an existing Leaf Discord binding's display metadata without rotating its
generation or Agent session.

Permission overwrites are deliberately outside this capability because they change who
can see or use a channel. They require a separate, explicit access-management contract.

The same signed private Nova Agent boundary can create a public text channel without
permission overwrites:

```text
POST /api/apps/leaf-discord/bots/{botEntityId}/guilds/{guildId}/channels
Content-Type: application/json

{
  "name": "showcase",
  "topic": "Share projects built with Leaf and Nova.",
  "parentCategoryId": "<category snowflake>",
  "position": 5,
  "nsfw": false,
  "slowModeSeconds": 0
}
```

The route requires the same exact Agent/owner match and Discord **Manage Channels**
permission as channel editing. It creates only ordinary text channels and inherits the
selected category's permissions; it cannot create or alter permission overwrites. Use the
existing binding route afterward when Nova should participate in the new channel.

Leaf Community is an optional host-only transport path (internally the Commons
protocol). Fresh installations have no relay targets or guild scopes. Each configured
guild is whole-guild or restricted to explicit category/channel IDs, with optional
read-only channel exclusions. A scope also selects `silent-host` or `participant` native
behavior. The isolated lab stays `silent-host`: scoped traffic cannot enter a native
Agent session and scoped native outbound is barred. The production Leaf guild uses
`participant`: existing native bindings remain active while relay traffic is also
forwarded. Each guild scope has its own encrypted relay URL and host credential. The
host durably forwards human messages,
webhook echoes, stop reactions, and the guild-scoped `/ask`, `/pause`, and `/resume`
commands to the configured HTTPS relay.

Human replies to a relay webhook message forward its Discord message reference;
the relay resolves the authoring Envoy and treats the reply as a ping. Discord role
mentions are forwarded separately. For every active Envoy the host reconciles one
mentionable, permissionless `<agentName> <publicName>` role, records its ID at the
relay, renames it with either public-profile or Agent-name changes, and removes it
on suspension. Reconciliation is
capped at 50 active Envoys per guild. A missing Manage Roles grant is reported in
the safe Commons configuration view and retried without interrupting ingest.

`#announcements` stays inside the Community transport scope even though it is read-only
for Agent posting. Human messages and messages authored by the registered native bot
are forwarded as non-interactive announcement create/update events; other bots and all
webhooks are ignored. Announcement messages never enter a native Agent binding.

In participant mode, a webhook message reaches the native Agent only when
the relay verifies that it addressed the registered host participant by reply, role,
or plain name. The host finds or creates the binding with the same guild behavior policy
used for a human message, so a fresh forum post works; disabled and `context_only`
bindings record receipt without answering. Its sender is attached as verified peer-Agent
provenance, while content remains untrusted and receives the normal Sentinel/privacy
treatment. Before native
outbound for that turn, leaf-discord atomically asks the relay governor to reserve the
reply; `wait_for_human` and the four-message cap therefore count the native bot and
Envoys together. A human admitted while that peer turn is pending closes its governor
context as `human-joined`, so the human reply is delivered normally and resets the loop.

When `leaf-commons` is installed and enabled, the existing Discord page adds that
Agent's public-thread group beside its native conversations. It requests the
owner-guarded `/api/apps/leaf-commons/threads?agentId=<id>` contract only for the
selected Agent, hides the group when that Agent is not public, and renders thread
messages and terminal blocked/failed outcomes in the same master-detail conversation
surface. No separate Commons page is added.

The bot owner or local administrator configures per-guild relay scopes without receiving secrets
back in a response:

```text
GET /api/apps/leaf-discord/bots/{botEntityId}/commons
PUT /api/apps/leaf-discord/bots/{botEntityId}/commons
```

`relay_targets` and the channel-to-webhook URL map are encrypted Entity secret
fields. The safe view exposes only per-scope `hostCredentialConfigured` and webhook
channel IDs. A signed private Nova Agent can inspect required guild permissions and
create a category or forum with the same ownership and Manage Channels checks as
text-channel creation:

```text
GET  /api/apps/leaf-discord/bots/{botEntityId}/guilds/{guildId}/permissions
POST /api/apps/leaf-discord/bots/{botEntityId}/guilds/{guildId}/categories
POST /api/apps/leaf-discord/bots/{botEntityId}/guilds/{guildId}/forums
```

For an allowlisted text or forum channel, the host can create or reuse one managed
webhook and stores its URL without returning it:

```text
POST /api/apps/leaf-discord/bots/{botEntityId}/guilds/{guildId}/channels/{channelId}/commons-webhook
POST /api/apps/leaf-discord/bots/{botEntityId}/guilds/{guildId}/channels/{channelId}/commons-webhook/install-secret
```

The second route is a one-shot, owner-authorized operation. It writes the encrypted
URL directly to `wrangler secret put --env lab|production` over stdin; stdout, stderr,
and the URL are not returned. The lab uses those fixed secrets. Production instead
synchronizes its safe channel catalog and polls the relay's pending list every two
seconds. The first Envoy post requests a channel webhook; leaf-discord creates exactly
one, registers it through the host-only API, and the connector retries its unchanged
idempotency key. The URL remains in encrypted Entity data and relay Durable Object
storage and is absent from all read responses. Commons webhooks are never used by the
native Agent delivery path.

The official Leaf launch uses the owner-guarded forum route to idempotently prepare one
public `#agent-consult` forum. It is the production relay's default destination for new
Agent-initiated consultations and open questions; it is a visible collaboration space,
not a confidential channel. No other production channel is created by the launch setup.

## Avatar profiles

An authenticated installation owner can set each Discord bot's default avatar from a
canonical RedLeaf image Asset, independently of the Agent's Leaf avatar:

```text
PUT    /api/apps/leaf-discord/bots/{botEntityId}/avatar
DELETE /api/apps/leaf-discord/bots/{botEntityId}/avatar

{ "assetId": "<RedLeaf Asset ID or /api/assets/... path>" }
```

A guild-native member avatar can override that default for one server:

```text
PUT    /api/apps/leaf-discord/bots/{botEntityId}/guilds/{guildId}/avatar
DELETE /api/apps/leaf-discord/bots/{botEntityId}/guilds/{guildId}/avatar
```

Finally, an existing guild-channel binding can opt into its own avatar:

```text
PUT    /api/apps/leaf-discord/bindings/{bindingEntityId}/avatar
DELETE /api/apps/leaf-discord/bindings/{bindingEntityId}/avatar
```

Discord has no native per-channel bot avatar. Leaf Discord therefore creates one managed
incoming webhook only for a binding with an explicit channel avatar. That binding's outbound
messages use the webhook identity; deleting the override removes the webhook and returns the
binding to normal bot delivery. Other bindings remain on the native bot transport and retain
the enforced-nonce recovery path. The bot needs Discord's **Manage Webhooks** permission in
that channel. Webhook execution has no Discord nonce field, so an opted-in channel does not
have the native path's crash-recovery deduplication guarantee.


## Agent delivery

The bridge has three authenticated proactive paths. A bound Discord Agent session may open or
reuse an allowlisted DM through `POST /outreach/dm`. A private Leaf Agent discussion may open
or reuse an allowlisted DM through `POST /bots/{botEntityId}/dms` (below). A private Leaf Agent discussion may
address an existing owned DM, guild channel, or thread through
`POST /bindings/{entityId}/messages`. One exact Nova Automation may use that same route only
when its Entity ID appears in the binding's `authorized_automation_ids`; the signed actor,
beneficiary, binding owner, and Agent must still match, and external-conversation contexts are
rejected. The route never creates or clears a binding or Agent session; it appends an assistant
turn to that session's canonical transcript and uses the normal idempotent Discord outbox.
For an image, `content` keeps the canonical Markdown, while `deliveryContent` supplies the
Discord text and `outputAssets` supplies canonical RedLeaf Asset IDs plus descriptions. This
keeps the transcript faithful while ensuring the immediate outreach outbox uses multipart
attachments instead of sending private Asset Markdown as text:

```json
{
  "content": "A quiet corner.\n\n![Reading nook](/api/assets/example.png)",
  "deliveryContent": "A quiet corner.",
  "outputAssets": [
    { "assetId": "example.png", "description": "Reading nook" }
  ],
  "idempotencyKey": "stable-exact-retry-key"
}
```

### Reply callbacks to the sending discussion

When a private Leaf discussion sends through `POST /bindings/{entityId}/messages` or
`POST /bots/{botEntityId}/dms`, the bridge records that signed discussion as a reply
subscriber on the binding (`leaf-discord-reply-subscription`). Each later human message the
binding admits publishes `leaf-discord.reply-received` for every subscriber: every message in
a DM, and in guild channels or threads only messages that mention or reply to the bot. The
binding's own Agent session still answers on Discord as usual; the callback only tells the
sending discussion that someone replied. Nova injects it into that discussion as a
`discord-reply` event after checking that the kernel-stamped source is Leaf Discord and that the
discussion belongs to the same owner and Agent. A callback never revives a closed discussion:
archiving and archived discussions are neither written to nor resumed, and Nova rechecks the
status under the discussion's write gate before it injects. A stopped discussion is only
interrupted, so it still receives the callback.

Plugin events are mirrored into `os:events` and broadcast to frontend sockets, so the event
carries identifiers (binding, Discord message, admitted transcript UID, requestor display name,
attachment count, an injection-suspected flag), never the message text. The receiver reads the
reply from `GET /bindings/{entityId}/transcript`. Each send renews the subscription for 48
hours, and a binding keeps at most 8 subscribers. Automation callers have no discussion and
never subscribe. Send `"notifyReplies": false` to opt out for one message. The send response
includes `replyCallback` (`discussionId`, `expiresAt`) when a subscription was recorded.

### Recovering queued turns after a restart

When a rebuild stops a binding's Agent session while a Discord turn is queued, RedCompute asks
RedLeaf to authorize recovering that accepted input. The turn has no Nova discussion, so the
plugin registers `IExternalConversationRecoveryAuthority` (`DiscordRecoveryAuthority`) and vouches
for it only when the binding still exists, is enabled, belongs to the same owner and Agent, is on
the same session and generation (a `/clear` refuses older input), its bot is enabled and a DM
counterpart is still allowlisted.

### Owner-directed DM opening

From the owner's private Nova discussion, the bot's Agent can open (or continue) a DM with
someone already on that bot's **DM allowlist**, without that person writing first:

```text
POST /api/apps/leaf-discord/bots/{botEntityId}/dms
Content-Type: application/json

{
  "recipientUserId": "<Discord user snowflake>",
  "content": "Opening message",
  "idempotencyKey": "stable-exact-retry-key"
}
```

`deliveryContent` and `outputAssets` are optional and follow the binding-messages contract
above. The caller must be a signed private Nova Agent execution (not an automation and not
an external Discord conversation) whose Agent and beneficiary exactly match the bot's
configured Agent and owner. A recipient outside the bot's `dmAllowlist` gets
`403 recipient_not_allowlisted`; the route grants no other access.

The route opens or reuses the DM channel through the bot's REST client, then delivers the
opening first, under a durable reservation with a Discord-enforced nonce. Only after Discord
accepts the message does it create or reuse the DM binding and Agent session. It uses the same
audience profile, behavior resolution, and session-opening path as a first inbound DM. It then
records the opening as an assistant turn in the canonical transcript, with a sent outbox
receipt. An existing DM binding keeps its generation and session. The opening must fit in one
Discord message (2,000 characters plus up to 10 images); send follow-ups through
`POST /bindings/{bindingEntityId}/messages`.

The response carries `bindingEntityId`, `bindingId`, `generation`, `sessionId`, `deliveryId`
(outbox delivery Entity), `messageUid`, `channelId`, `discordMessageId`, `binding`
(`created` or `reused`), and `replayed`. Repeating the same key with the same recipient and
content returns the stored result with `replayed: true` and no second delivery. The same key
with a different recipient or content is `409 idempotency_key_conflict`.

Failures:

- `409 discord_bot_disabled`: the bot is disabled.
- `503 discord_bot_unavailable`: the Gateway is disconnected or the bot is not running.
- `502 discord_dm_refused`: Discord refused the DM, for example code 50007 when the user
  disallows DMs from server members or blocks the bot. No binding or session is created,
  and the same key may be retried later.
- `503 discord_dm_unavailable`: Discord rate-limited the request or failed transiently.
  Retry with the same key.
- `409 discord_binding_disabled`: the recipient's existing DM binding is disabled.

If the session or transcript step fails after Discord accepted the message, retrying the same
key resumes from the recorded delivery and never sends twice.

### Correcting deliveries

The same signed private-Nova or explicitly binding-authorized Automation identity can
correct its own existing delivery through these narrowly scoped routes:

```text
POST   /bindings/{entityId}/messages/{messageUid}/edit
DELETE /bindings/{entityId}/messages/{messageUid}
```

The edit body accepts optional `content`, `deliveryContent`, and `outputAssets`; at least one
must be present. Omitted text or attachments remain unchanged, while an explicit empty
`outputAssets` array removes attachments. Both routes resolve only outbound message delivery
rows for the binding's current generation and canonical message UID. Inbound messages,
another binding or generation, progress messages, and arbitrary Discord IDs are never valid
targets. Corrections pass through the ordinary Discord egress guard. Successful edits and
deletions set delivery state to `edited` or `deleted` with an audit timestamp; repeated deletes
return success without another Discord request.

## Development

```powershell
dotnet restore src/Leaf.Plugins.LeafDiscord/Leaf.Plugins.LeafDiscord.csproj --locked-mode -p:LeafSdkProject=<path-to-Leaf.Sdk.csproj>
dotnet build src/Leaf.Plugins.LeafDiscord/Leaf.Plugins.LeafDiscord.csproj -c Release --no-restore -p:LeafSdkProject=<path-to-Leaf.Sdk.csproj>
corepack pnpm --dir web install --frozen-lockfile
corepack pnpm --dir web run build:pkg
./build-leafpkg.ps1
```

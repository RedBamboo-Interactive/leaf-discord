export const DISCORD_BINDING_PAGE_SIZE = 24

export function discordBindingsRequestPath(
  agentId: string | null,
  cursor: string | null,
): string {
  const query = new URLSearchParams({ limit: String(DISCORD_BINDING_PAGE_SIZE) })
  if (agentId) query.set("agentId", agentId)
  if (cursor) query.set("cursor", cursor)
  return `/bindings?${query.toString()}`
}

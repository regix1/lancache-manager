/**
 * Shared classification for game-removal notifications (Steam / Epic / named
 * [Blizzard/Riot/Xbox]). Extracted because the epic/steam/named decision, its associated
 * i18n stage-key naming (epicRemove.* / gameRemove.* / namedRemove.* for the starting|complete
 * phases), and the conditional identity-field spread (`{gameName, ...(steam && {gameAppId}),
 * ...(epic && {epicAppId})}`) were duplicated across detailMessageFormatters.ts,
 * notificationRegistry.ts, and recoveryFactory.ts and had to be kept in lockstep by hand.
 */

type RemovalKind = 'epic' | 'steam' | 'named';

interface RemovalKindEntity {
  gameAppId?: number | null;
  epicAppId?: string | null;
  entityKind?: 'steam' | 'epic' | 'named' | null;
}

/**
 * Classifies a removal entity into epic/steam/named. `entityKind` (only present on the REST
 * batch-recovery payload) is consulted first; SignalR event payloads carry no entityKind
 * field at all, so for them this reduces to the epicAppId-truthy / positive-gameAppId checks
 * every call site already used on its own.
 */
export function classifyRemovalKind(entity: RemovalKindEntity): RemovalKind {
  const isEpic = entity.entityKind === 'epic' || !!entity.epicAppId;
  if (isEpic) {
    return 'epic';
  }
  if (typeof entity.gameAppId === 'number' && entity.gameAppId > 0) {
    return 'steam';
  }
  return 'named';
}

/** i18n stage key for a removal kind at a given lifecycle phase. */
export function removalStageKey(kind: RemovalKind, phase: 'starting' | 'complete'): string {
  const family = kind === 'epic' ? 'epicRemove' : kind === 'steam' ? 'gameRemove' : 'namedRemove';
  return `signalr.${family}.${phase}`;
}

/**
 * Appends the scope-exclusive identity field (gameAppId for steam, epicAppId for epic,
 * neither for named) onto a base context/details object. `epicAppId` is only added when
 * truthy - even for an entity classified 'epic' via entityKind alone - matching the recovery
 * batch path's original guard.
 */
export function withRemovalIdentity<T extends object>(
  base: T,
  kind: RemovalKind,
  gameAppId?: number | null,
  epicAppId?: string | null
): T & { gameAppId?: number; epicAppId?: string } {
  return {
    ...base,
    ...(kind === 'steam' && typeof gameAppId === 'number' && { gameAppId }),
    ...(kind === 'epic' && epicAppId && { epicAppId })
  };
}

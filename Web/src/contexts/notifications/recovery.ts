/**
 * Detail recovery. After a reload or a reconnect a run card is drawn from its run row alone, which
 * lacks what the per-type events carry: the entity ids that keep a page's buttons busy, the prefill
 * platform and schedule, the stage line, byte counts. The per-type status endpoints still answer
 * with those fields, and the registry's `recovery` configs map each answer into a detail patch here.
 *
 * Recovery only fills in detail. It never opens, ends, reconciles or restyles a card: the server's
 * run rows do that.
 */

import type { NotificationType, SimpleRecoveryConfig, UnifiedNotification } from './types';
import type { RunDetail } from './runStore';
import { REMOVING_GAME_I18N_KEY } from './constants';
import { NOTIFICATION_REGISTRY } from './notificationRegistry';
import { classifyRemovalKind, removalStageKey, withRemovalIdentity } from './removalKind';
import i18n from '@/i18n';
import type { CorruptionDetectionMethod } from '@/types';

export type FetchWithAuth = (url: string) => Promise<Response>;

/** Hands one recovered detail to the store: the run it names, or the one live run of that type. */
type ApplyRecovered = (
  type: NotificationType,
  operationId: string | undefined,
  detail: RunDetail
) => void;

type RecoveredCard = Omit<UnifiedNotification, 'id' | 'type' | 'status' | 'startedAt'>;

/**
 * A recovered card as a detail patch. Only what the response actually carries is kept: a field it
 * leaves undefined must not erase what the card already shows.
 */
function recoveredDetail(card: RecoveredCard): RunDetail {
  const detail: RunDetail = {
    message: card.message,
    details: Object.fromEntries(
      Object.entries(card.details ?? {}).filter(([, value]) => value !== undefined)
    ) as UnifiedNotification['details']
  };
  if (card.detailMessage !== undefined) detail.detailMessage = card.detailMessage;
  if (card.progress !== undefined) detail.progress = card.progress;
  if (card.progressMode !== undefined) detail.progressMode = card.progressMode;
  if (card.progressAriaValueText !== undefined)
    detail.progressAriaValueText = card.progressAriaValueText;
  return detail;
}

// ============================================================================
// Simple recovery (one status endpoint per type)
// ============================================================================
// The createNotification/recoverCards/isProcessing readers in the config access REST response
// property names directly (snake_case/camelCase as the wire delivers them) and are intentionally NOT
// normalized against SignalR event property names - a field may cross both boundaries with
// different casing.

/** Resolves false when the endpoint could not be read, so the provider asks again later. */
function createSimpleRecoveryFunction<TData>(
  config: SimpleRecoveryConfig<TData>,
  type: NotificationType,
  fetchWithAuth: FetchWithAuth,
  apply: ApplyRecovered
): () => Promise<boolean> {
  return async () => {
    const response = await fetchWithAuth(config.apiEndpoint);
    if (response.status === 401 || response.status === 403) return true;
    if (!response.ok) return false;
    const status = (await response.json()) as TData;
    if (!config.isProcessing(status)) return true;
    const cards = config.recoverCards
      ? config.recoverCards(status)
      : [config.createNotification(status)];
    for (const card of cards) apply(type, card.details?.operationId, recoveredDetail(card));
    return true;
  };
}

// ============================================================================
// Cache Removals Recovery (handles multiple types via ONE endpoint)
// ============================================================================
// game_removal, service_removal, corruption_removal, and eviction_removal are
// all recovered by a SINGLE GET to /api/cache/removals/active. Their registry
// entries carry `recovery: { kind: 'cacheRemovalsBatch' }` as a marker; the
// runner issues this fetch exactly once for the whole group.

interface CacheRemovalOperation {
  gameAppId?: number | null;
  epicAppId?: string | null;
  entityKind?: 'steam' | 'epic' | 'named' | null;
  gameName?: string;
  serviceName?: string;
  service?: string;
  operationId?: string;
  message?: string;
  startedAt?: string;
  filesDeleted?: number;
  bytesFreed?: number;
  status?: string;
  detectionMethod?: CorruptionDetectionMethod;
}

// REST shape returned by /api/cache/removals/active for eviction_removal entries.
// scope/key/gameName are camelCase because AllActiveRemovalsResponse uses the global
// JsonNamingPolicy.CamelCase (no [JsonPropertyName] overrides on EvictionRemovalInfo).
interface EvictionRemovalOperation {
  operationId?: string;
  scope?: string; // "steam" | "epic" | "service" | null (bulk)
  key?: string; // steamAppId as string, epicAppId, service name, or null for bulk
  gameName?: string; // resolved display name for steam/epic scopes
  message?: string;
  startedAt?: string;
}

interface CacheRemovalsData {
  isProcessing: boolean;
  gameRemovals?: CacheRemovalOperation[];
  serviceRemovals?: CacheRemovalOperation[];
  corruptionRemovals?: CacheRemovalOperation[];
  evictionRemovals?: EvictionRemovalOperation[];
}

function createCacheRemovalsRecoveryFunction(
  fetchWithAuth: FetchWithAuth,
  apply: ApplyRecovered
): () => Promise<boolean> {
  return async () => {
    const response = await fetchWithAuth('/api/cache/removals/active');
    if (response.status === 401 || response.status === 403) return true;
    if (!response.ok) return false;

    const removals = (await response.json()) as CacheRemovalsData;

    // Recover game removals.
    // Post-Phase-2 contract: game_removal rehydrates scope-aware identity. Steam entries
    // emit details.gameAppId (number); Epic entries emit details.epicAppId (string); named
    // entries (blizzard/riot/xbox, entityKind==='named') emit neither - identity lives in
    // gameName/service, not an appId. Ops missing ALL THREE (no epicAppId, no positive
    // gameAppId, no entityKind:'named') are legacy/pre-Phase-2 data only - logged and skipped.
    const recoverableGameRemovals = (removals.gameRemovals ?? []).filter((op) => {
      if ((op.entityKind === 'epic' || op.epicAppId) && op.epicAppId) return true;
      if (typeof op.gameAppId === 'number' && op.gameAppId > 0) return true;
      if (op.entityKind === 'named') return true;
      console.warn('[recovery] Skipping game_removal op with no scope identity:', op.operationId);
      return false;
    });
    recoverOperations(
      recoverableGameRemovals,
      'game_removal',
      (op) => {
        // Named (blizzard/riot/xbox): neither epic nor a positive Steam AppId. Reuses the
        // existing signalr.namedRemove.* family (already wired into GamesController's
        // Started/Complete events for this same path) - AppID-free, matches epicRemove shape.
        const kind = classifyRemovalKind(op);
        const stageKey = removalStageKey(kind, 'starting');
        const context = withRemovalIdentity(
          { gameName: op.gameName ?? '' },
          kind,
          op.gameAppId,
          op.epicAppId
        );
        const baseDetails = {
          operationId: op.operationId,
          gameName: op.gameName ?? '',
          ...(op.service && { service: op.service }),
          stageKey,
          filesDeleted: op.filesDeleted,
          bytesFreed: op.bytesFreed
        };
        const details = withRemovalIdentity(baseDetails, kind, op.gameAppId, op.epicAppId);
        return {
          message: i18n.t(stageKey, context),
          details
        };
      },
      apply
    );

    // Recover service removals
    recoverOperations(
      removals.serviceRemovals,
      'service_removal',
      (op) => ({
        message: i18n.t('signalr.serviceRemove.starting.default', {
          service: op.serviceName ?? ''
        }),
        details: {
          operationId: op.operationId,
          service: op.serviceName,
          filesDeleted: op.filesDeleted,
          bytesFreed: op.bytesFreed
        }
      }),
      apply
    );

    // Recover corruption removals
    recoverOperations(
      removals.corruptionRemovals,
      'corruption_removal',
      (op) => ({
        message: i18n.t(
          op.detectionMethod === 'structural'
            ? 'signalr.corruptionRemove.startingStructural'
            : 'signalr.corruptionRemove.starting',
          { service: op.service ?? '' }
        ),
        details: {
          operationId: op.operationId,
          service: op.service,
          detectionMethod: op.detectionMethod
        }
      }),
      apply
    );

    // Recover eviction removals.
    // Scope-to-identifier mapping (mirrors notificationRegistry.ts getDetails for EvictionRemovalStarted):
    //   steam   → gameAppId: Number(key), steamAppId: key, gameName (optional)
    //   epic    → epicAppId: key, service: 'epicgames', gameName (optional)
    //   named   → service: key, gameName
    //   service → service: key
    //   null    → bulk removal, no identifier fields needed beyond operationId
    // REST payload uses camelCase (global JsonNamingPolicy.CamelCase on AllActiveRemovalsResponse).
    // SignalR events use camelCase too - but the field semantics differ slightly (see registry comment).
    recoverEvictionRemovals(removals.evictionRemovals, apply);
    return true;
  };
}

// Eviction removal recovery is scope-aware and cannot use the generic recoverOperations
// helper because the details shape differs per scope (steam/epic/service/bulk).
function recoverEvictionRemovals(
  operations: EvictionRemovalOperation[] | undefined,
  apply: ApplyRecovered
): void {
  for (const op of operations ?? []) {
    const scope = op.scope?.toLowerCase();
    const key = op.key;
    apply(
      'eviction_removal',
      op.operationId,
      recoveredDetail({
        message:
          op.gameName !== undefined
            ? i18n.t(REMOVING_GAME_I18N_KEY, { name: op.gameName })
            : scope !== undefined && key !== undefined
              ? i18n.t('signalr.evictionRemove.starting.entity', { scope, key })
              : i18n.t('signalr.evictionRemove.starting.bulk', {}),
        details: {
          operationId: op.operationId,
          ...(op.gameName !== undefined && { gameName: op.gameName }),
          ...(scope === 'steam' &&
            key !== undefined && { gameAppId: Number(key), steamAppId: key }),
          ...(scope === 'epic' && key !== undefined && { epicAppId: key }),
          ...((scope === 'service' || scope === 'named') && key !== undefined && { service: key }),
          ...(scope === 'epic' && { service: 'epicgames' })
        }
      })
    );
  }
}

function recoverOperations(
  operations: CacheRemovalOperation[] | undefined,
  type: NotificationType,
  createData: (op: CacheRemovalOperation) => RecoveredCard,
  apply: ApplyRecovered
): void {
  for (const op of operations ?? []) apply(type, op.operationId, recoveredDetail(createData(op)));
}

// ============================================================================
// Recovery Runner Factory
// ============================================================================

/**
 * Creates the detail recovery runner. Each call reads the status endpoints of the given types (one
 * GET per endpoint, not per run) and hands every mapped result to `apply`:
 *   - kind:'simple' → that entry's own endpoint
 *   - kind:'cacheRemovalsBatch' → one GET to /api/cache/removals/active for the whole group
 *   - kind:'none' → no recovery
 * It resolves false when any endpoint could not be read.
 *
 * @param fetchWithAuth - Authenticated fetch function
 */
export function createRecoveryRunner(
  fetchWithAuth: FetchWithAuth
): (types: ReadonlySet<NotificationType>, apply: ApplyRecovered) => Promise<boolean> {
  return async (types, apply) => {
    const runs: (() => Promise<boolean>)[] = [];
    let needsCacheRemovalsBatch = false;
    for (const entry of NOTIFICATION_REGISTRY) {
      if (!types.has(entry.type)) continue;
      if (entry.recovery.kind === 'cacheRemovalsBatch') needsCacheRemovalsBatch = true;
      if (entry.recovery.kind === 'simple')
        runs.push(createSimpleRecoveryFunction(entry.recovery, entry.type, fetchWithAuth, apply));
    }
    if (needsCacheRemovalsBatch)
      runs.push(createCacheRemovalsRecoveryFunction(fetchWithAuth, apply));
    const results = await Promise.all(
      runs.map((run) =>
        run().catch((error: unknown) => {
          console.warn('Unable to recover run details', error);
          return false;
        })
      )
    );
    return results.every(Boolean);
  };
}

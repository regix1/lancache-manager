import { useCallback, useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import ApiService from '@services/api.service';
import { useAuth } from '@contexts/useAuth';
import { useSignalR } from '@contexts/SignalRContext/useSignalR';
import type { SignalREventName } from '@contexts/SignalRContext/types';
import { isAbortError } from '@utils/error';
import { useErrorHandler } from './useErrorHandler';
import { useReconnectRefetch } from './useReconnectRefetch';

interface KnownClientIps {
  clientIps: string[];
  loading: boolean;
  /** Reloads if the list has gone stale. Safe to call whenever the list becomes visible. */
  refresh: () => void;
}

/** A group edit emits one update event plus one per member, so the burst is coalesced. */
const REFRESH_DEBOUNCE_MS = 1000;

/**
 * How long a loaded list is treated as current for a caller with no reason to think it changed.
 * The request is a `GROUP BY ClientIp` over the whole Downloads table, so it must not repeat on
 * every section open. Events bypass this window, so a user's own edit never waits on it.
 */
const STALE_AFTER_MS = 30_000;

/** Matches the API's own ceiling: the server clamps this to its configured MaxClientsPerRequest. */
const CLIENT_LIMIT = 2147483647;

/** A request that never settles would hold the in-flight slot and leave the spinner up. */
const LOAD_TIMEOUT_MS = 30_000;

const CLIENT_GROUP_EVENTS: readonly SignalREventName[] = [
  'ClientGroupCreated',
  'ClientGroupUpdated',
  'ClientGroupDeleted',
  'ClientGroupMemberAdded',
  'ClientGroupMemberRemoved',
  'ClientGroupsCleared'
];

/**
 * The client IPs behind the Management > Clients pickers, kept in step with client group changes.
 *
 * Fetched without time params: management surfaces must not be narrowed by the dashboard's range.
 * A group event means the rows changed, so it reloads unconditionally; `refresh` only asks to be
 * current, which a load in flight or a recent one already satisfies.
 */
export function useKnownClientIps(): KnownClientIps {
  const { authMode, isLoading: authLoading } = useAuth();
  const { on, off, isConnected } = useSignalR();
  const { notifyError } = useErrorHandler();
  const { t } = useTranslation();
  const [clientIps, setClientIps] = useState<string[]>([]);
  const [loading, setLoading] = useState(true);
  const mountedRef = useRef(true);
  const authModeRef = useRef(authMode);
  const loadRef = useRef<((force: boolean) => Promise<void>) | undefined>(undefined);
  const refreshTimerRef = useRef<ReturnType<typeof setTimeout> | null>(null);
  // Doubles as the in-flight flag and as the token deciding which response may publish.
  const abortControllerRef = useRef<AbortController | null>(null);
  const loadedAtRef = useRef(0);

  /** `force` means the rows are known to have changed, so freshness and coalescing are bypassed. */
  const load = useCallback(
    async (force: boolean) => {
      if (!mountedRef.current) return;
      const mode = authModeRef.current;
      if (mode !== 'authenticated' && mode !== 'guest') return;

      if (abortControllerRef.current) {
        // Restarting a load already fetching these rows would only delay them, and repeated
        // triggers could starve it. A forced load supersedes: that answer is already out of date.
        if (!force) return;
        abortControllerRef.current.abort();
      } else if (!force && Date.now() - loadedAtRef.current < STALE_AFTER_MS) {
        return;
      }

      const controller = new AbortController();
      const abortOnTimeout = () => controller.abort();
      let loadTimeout: AbortSignal | null = null;

      try {
        // Claimed inside the try so the finally below always releases it.
        abortControllerRef.current = controller;
        // Routed through this load's controller so a timeout ends as an ordinary abort.
        loadTimeout = AbortSignal.timeout(LOAD_TIMEOUT_MS);
        loadTimeout.addEventListener('abort', abortOnTimeout, { once: true });
        const stats = await ApiService.getClientStats(
          controller.signal,
          undefined,
          undefined,
          undefined,
          true,
          undefined,
          CLIENT_LIMIT
        );
        // An abort after the response arrived cannot reject, so ownership is re-checked here.
        if (mountedRef.current && abortControllerRef.current === controller) {
          loadedAtRef.current = Date.now();
          setClientIps(stats.map((stat) => stat.clientIp));
        }
      } catch (err) {
        // Supersession, unmount, session loss and the timeout all arrive as the same cancel.
        if (isAbortError(err)) {
          return;
        }
        // The previous rows stay on screen and the failure is silent, so the freshness window
        // must stop vouching for them and let the next caller retry.
        if (abortControllerRef.current === controller) {
          loadedAtRef.current = 0;
        }
        notifyError(
          t('management.sections.clients.errors.failedToLoadIps', 'Failed to load client IPs'),
          err,
          {
            silent: true,
            logLabel: 'Failed to fetch all client IPs'
          }
        );
      } finally {
        loadTimeout?.removeEventListener('abort', abortOnTimeout);
        // Only the current owner reports completion, so a superseded load cannot clear the
        // spinner or release the slot its replacement holds.
        if (abortControllerRef.current === controller) {
          abortControllerRef.current = null;
          if (mountedRef.current) setLoading(false);
        }
      }
    },
    [notifyError, t]
  );

  useEffect(() => {
    mountedRef.current = true;
    return () => {
      mountedRef.current = false;
      abortControllerRef.current?.abort();
      abortControllerRef.current = null;
    };
  }, []);

  useEffect(() => {
    // Written here rather than in its own effect so its ordering against the load below is defined.
    authModeRef.current = authMode;
    if (authLoading) return;
    if (authMode === 'authenticated' || authMode === 'guest') {
      void load(false);
    } else {
      abortControllerRef.current?.abort();
      abortControllerRef.current = null;
      // Signing back in must load again rather than trust the timestamp of discarded rows.
      loadedAtRef.current = 0;
      setClientIps([]);
      setLoading(false);
    }
  }, [authLoading, authMode, load]);

  useEffect(() => {
    loadRef.current = load;
  }, [load]);

  // Stable, so a caller can depend on it without its effect re-running for the hook's own reasons.
  const refresh = useCallback(() => {
    void loadRef.current?.(false);
  }, []);

  // Events raised while the socket was down are never delivered, which is exactly what the
  // freshness window cannot account for, so a genuine reconnect forces a reload.
  useReconnectRefetch(isConnected, () => {
    void loadRef.current?.(true);
  });

  useEffect(() => {
    const handleGroupsChanged = () => {
      if (refreshTimerRef.current) clearTimeout(refreshTimerRef.current);
      refreshTimerRef.current = setTimeout(() => {
        refreshTimerRef.current = null;
        void loadRef.current?.(true);
      }, REFRESH_DEBOUNCE_MS);
    };

    CLIENT_GROUP_EVENTS.forEach((eventName) => on(eventName, handleGroupsChanged));

    return () => {
      CLIENT_GROUP_EVENTS.forEach((eventName) => off(eventName, handleGroupsChanged));
      if (refreshTimerRef.current) {
        clearTimeout(refreshTimerRef.current);
        refreshTimerRef.current = null;
      }
    };
  }, [on, off]);

  return { clientIps, loading, refresh };
}

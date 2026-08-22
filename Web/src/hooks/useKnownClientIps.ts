import { useCallback, useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import ApiService from '@services/api.service';
import { useAuth } from '@contexts/useAuth';
import { useSignalR } from '@contexts/SignalRContext/useSignalR';
import type { SignalREventName } from '@contexts/SignalRContext/types';
import type { ClientStat } from '../types';
import { useErrorHandler } from './useErrorHandler';
import { useLoadLifecycle } from './useLoadLifecycle';
import { useReconnectRefetch } from './useReconnectRefetch';
import { useTimeoutCallback } from './useTimeoutCallback';

interface KnownClientIps {
  clientIps: string[];
  loading: boolean;
  /** Reloads if the list has gone stale. Safe to call whenever the list becomes visible. */
  refresh: () => void;
}

/** Successive group writes can land within a moment of each other, so the burst collapses into one load. */
const REFRESH_DEBOUNCE_MS = 1000;

/** Matches the API's own ceiling: the server clamps this to its configured MaxClientsPerRequest. */
const CLIENT_LIMIT = 2147483647;

const CLIENT_GROUP_EVENTS: readonly SignalREventName[] = [
  'ClientGroupCreated',
  'ClientGroupUpdated',
  'ClientGroupDeleted',
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
  const authModeRef = useRef(authMode);
  const scheduleReload = useTimeoutCallback(REFRESH_DEBOUNCE_MS);

  const { load, reset } = useLoadLifecycle<ClientStat[]>({
    canLoad: () => authModeRef.current === 'authenticated' || authModeRef.current === 'guest',
    request: (signal: AbortSignal) =>
      ApiService.getClientStats(
        signal,
        undefined,
        undefined,
        undefined,
        true,
        undefined,
        CLIENT_LIMIT
      ),
    onLoaded: (stats: ClientStat[]) => {
      setClientIps(stats.map((stat) => stat.clientIp));
    },
    onFailed: (error: unknown) => {
      notifyError(t('management.sections.clients.errors.failedToLoadIps'), error, {
        silent: true,
        logLabel: 'Failed to fetch all client IPs'
      });
    },
    onSettled: () => {
      setLoading(false);
    }
  });

  useEffect(() => {
    // Written here rather than in its own effect so its ordering against the load below is defined.
    authModeRef.current = authMode;
    if (authLoading) return;
    if (authMode === 'authenticated' || authMode === 'guest') {
      void load(false);
    } else {
      reset();
      setClientIps([]);
      setLoading(false);
    }
  }, [authLoading, authMode, load, reset]);

  // Stable, so a caller can depend on it without its effect re-running for the hook's own reasons.
  const refresh = useCallback(() => {
    void load(false);
  }, [load]);

  // Events raised while the socket was down are never delivered, which is exactly what the
  // freshness window cannot account for, so a genuine reconnect forces a reload.
  useReconnectRefetch(isConnected, () => {
    void load(true);
  });

  useEffect(() => {
    const handleGroupsChanged = () => {
      scheduleReload(() => {
        void load(true);
      });
    };

    CLIENT_GROUP_EVENTS.forEach((eventName) => on(eventName, handleGroupsChanged));

    return () => {
      CLIENT_GROUP_EVENTS.forEach((eventName) => off(eventName, handleGroupsChanged));
    };
  }, [on, off, load, scheduleReload]);

  return { clientIps, loading, refresh };
}

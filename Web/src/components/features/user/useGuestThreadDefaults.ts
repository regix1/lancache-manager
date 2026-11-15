import { useCallback, useEffect, useRef, useState } from 'react';
import ApiService from '@services/api.service';
import { assertOk } from '@services/apiError';
import { useSignalR } from '@contexts/SignalRContext/useSignalR';
import { useReconnectRefetch } from '@hooks/useReconnectRefetch';
import { getErrorMessage } from '@utils/error';
import {
  PREFILL_SERVICES,
  prefillServiceRecord,
  type GuestPrefillConfigChangedPayload,
  type PrefillServiceConfig
} from '@components/features/prefill/hooks/prefillServiceConfig';
import type { GameServiceId } from '@/types/gameService';
import { type GuestPrefillConfigResponse, toGuestPrefillConfig } from './guestPrefillConfig';

/** The install-wide guest thread limits a guest pane shows beside its own, and their read. */
export interface GuestThreadDefaults {
  values: Record<GameServiceId, number | null>;
  error: string | null;
  retrying: boolean;
  retry: () => Promise<void>;
}

/**
 * Reads the guest thread limits once for the Users page rather than once per guest pane, since a
 * switch between guests remounts the pane but changes none of them. Kept apart from the page's
 * other guest defaults because only a guest pane needs these five reads. `guestSessionId` is the
 * shown pane's session when it is a guest: picking one reads while nothing has been read yet or
 * the last read failed.
 */
export const useGuestThreadDefaults = (guestSessionId: string | null): GuestThreadDefaults => {
  const { on, off, isConnected } = useSignalR();
  const [values, setValues] = useState<Record<GameServiceId, number | null>>(() =>
    prefillServiceRecord<number | null>(() => null)
  );
  const [error, setError] = useState<string | null>(null);
  // A Retry's read can be replaced by a reconnect read, so the Retry stays busy until the newest
  // read settles.
  const [retrying, setRetrying] = useState(false);
  const requestRef = useRef(0);

  // Reconnect, a pick and Retry can overlap; only the newest request writes values or the error.
  const load = useCallback(async (): Promise<void> => {
    const request = ++requestRef.current;
    try {
      const loaded = await Promise.all(
        PREFILL_SERVICES.filter((service: PrefillServiceConfig) => service.supportsMaxThreads).map(
          async (service: PrefillServiceConfig) => {
            const response = await fetch(service.guestConfigPath, ApiService.getFetchOptions());
            await assertOk(response);
            const config = (await response.json()) as GuestPrefillConfigResponse;
            return {
              id: service.id,
              maxThreadCount: toGuestPrefillConfig(service, config).maxThreadCount
            };
          }
        )
      );
      if (request !== requestRef.current) return;
      setValues((previous: Record<GameServiceId, number | null>) => {
        const next = { ...previous };
        for (const item of loaded) next[item.id] = item.maxThreadCount;
        return next;
      });
      setError(null);
      setRetrying(false);
    } catch (err: unknown) {
      if (request !== requestRef.current) return;
      setError(getErrorMessage(err));
      setRetrying(false);
    }
  }, []);

  useEffect(() => {
    if (guestSessionId === null) return;
    if (requestRef.current > 0 && error === null) return;
    void load();
    // Only a new guest pick reads; the error is read at that moment, not watched.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [guestSessionId, load]);

  // Each service names its thread limit after itself on its own event.
  useEffect(() => {
    const subscriptions = PREFILL_SERVICES.flatMap((service: PrefillServiceConfig) =>
      service.supportsMaxThreads
        ? [
            {
              eventName: service.guestConfigChangedEvent,
              handler: (event: GuestPrefillConfigChangedPayload): void => {
                if (!(service.configEventThreadField in event)) return;
                const threadCount = event[service.configEventThreadField] ?? null;
                setValues((previous: Record<GameServiceId, number | null>) => ({
                  ...previous,
                  [service.id]: threadCount
                }));
              }
            }
          ]
        : []
    );
    for (const subscription of subscriptions) on(subscription.eventName, subscription.handler);
    return () => {
      for (const subscription of subscriptions) off(subscription.eventName, subscription.handler);
    };
  }, [on, off]);

  // A push missed while the socket was down is recovered by reading again, once a pane has asked.
  useReconnectRefetch(isConnected, () => {
    if (requestRef.current > 0) void load();
  });

  const retry = (): Promise<void> => {
    setRetrying(true);
    return load();
  };

  return { values, error, retrying, retry };
};

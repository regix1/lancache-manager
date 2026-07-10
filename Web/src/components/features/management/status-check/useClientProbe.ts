import { useCallback, useEffect, useState } from 'react';
import { isAbortError } from '@utils/error';
import { CLIENT_PROBE_TIMEOUT_MS, CLIENT_PROBE_URL } from './constants';
import type { ClientProbeState } from './types';

/**
 * Best-effort browser-side heartbeat probe ("From this device" card).
 *
 * Constraints this encodes (see the mixed-content / no-cors research):
 * - On an https: page the browser blocks plain-http fetches outright (mixed
 *   content applies below Fetch's CORS layer), so no probe is attempted.
 * - A cors-mode fetch can read X-LanCache-Processed-By ONLY when the cache's
 *   nginx opts in; interception still requires the exact heartbeat contract:
 *   HTTP 204 plus a non-empty readable header.
 * - A no-cors fetch yields an opaque response: it proves something answered,
 *   never WHAT answered, so it can only ever be "inconclusive".
 * - A rejected fetch (or the probe timing out) means "unreachable".
 */
export function useClientProbe(): { state: ClientProbeState; retry: () => void } {
  const [state, setState] = useState<ClientProbeState>({ status: 'checking', servedBy: null });
  const [attempt, setAttempt] = useState(0);

  useEffect(() => {
    if (window.location.protocol === 'https:') {
      setState({ status: 'blocked', servedBy: null });
      return;
    }

    let cancelled = false;
    const controller = new AbortController();
    const timer = setTimeout(() => controller.abort(), CLIENT_PROBE_TIMEOUT_MS);
    setState({ status: 'checking', servedBy: null });

    const probe = async (): Promise<void> => {
      try {
        const response = await fetch(CLIENT_PROBE_URL, {
          mode: 'cors',
          cache: 'no-store',
          signal: controller.signal
        });
        const servedBy = response.headers.get('X-LanCache-Processed-By')?.trim() ?? null;
        if (cancelled) return;
        if (response.status === 204 && servedBy) {
          setState({ status: 'intercepted', servedBy });
        } else {
          setState({ status: 'inconclusive', servedBy: null });
        }
      } catch (corsError) {
        if (isAbortError(corsError)) {
          // Timed out (or unmounted - guarded below): nothing answered in time.
          if (!cancelled) setState({ status: 'unreachable', servedBy: null });
          return;
        }
        // A cors-mode TypeError usually just means the server didn't send CORS
        // headers - retry opaquely to at least learn "reachable vs not".
        try {
          await fetch(CLIENT_PROBE_URL, {
            mode: 'no-cors',
            cache: 'no-store',
            signal: controller.signal
          });
          if (!cancelled) setState({ status: 'inconclusive', servedBy: null });
        } catch (_fallbackError) {
          if (!cancelled) setState({ status: 'unreachable', servedBy: null });
        }
      } finally {
        clearTimeout(timer);
      }
    };

    void probe();

    return () => {
      cancelled = true;
      controller.abort();
      clearTimeout(timer);
    };
  }, [attempt]);

  const retry = useCallback(() => setAttempt((previous) => previous + 1), []);

  return { state, retry };
}

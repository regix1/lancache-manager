import { useEffect, useRef } from 'react';

/**
 * Runs `onReconnect` only when the SignalR connection is genuinely RE-established (a
 * disconnected -> connected transition), never on the first connect and never when the
 * callback's identity changes (page/filter/busy state). This recovers a stale snapshot after a
 * socket drop swallowed a completion/change event, without double-fetching on mount or spamming
 * the endpoint every time an unrelated dependency updates.
 *
 * The callback is read through a ref so the effect depends only on `isConnected`. Put any
 * conditional guards (mock mode, an in-progress operation, admin-only endpoints) INSIDE the
 * callback the caller passes.
 */
export function useReconnectRefetch(isConnected: boolean, onReconnect: () => void): void {
  const everConnectedRef = useRef(false);
  const callbackRef = useRef(onReconnect);
  callbackRef.current = onReconnect;

  useEffect(() => {
    if (!isConnected) {
      return;
    }
    if (everConnectedRef.current) {
      // Was connected before, dropped, and is now back: a real reconnect.
      callbackRef.current();
    } else {
      // First time we have seen the connection up. Arm for the next drop; do not fetch (the
      // component's own mount effect already owns the initial load).
      everConnectedRef.current = true;
    }
  }, [isConnected]);
}

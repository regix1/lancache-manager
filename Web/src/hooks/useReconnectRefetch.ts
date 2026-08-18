import { useEffect, useRef } from 'react';

/**
 * Runs `onReconnect` on every transition into a live SignalR connection, including the first one,
 * so state fetched while the socket was down - or before it came up at all - is replaced by
 * authoritative state. The single connect it stays quiet for is a connection that was already live
 * when the caller mounted: that caller's own mount fetch was issued with the subscription running,
 * nothing could have been missed, and a refetch would only duplicate it.
 *
 * The callback is read through a ref so the effect depends only on `isConnected`, never on the
 * callback's identity (page/filter/busy state). Put any conditional guards (mock mode, an
 * in-progress operation, admin-only endpoints) INSIDE the callback the caller passes.
 */
export function useReconnectRefetch(isConnected: boolean, onReconnect: () => void): void {
  const connectedAtMountRef = useRef(isConnected);
  const callbackRef = useRef(onReconnect);
  callbackRef.current = onReconnect;

  useEffect(() => {
    if (!isConnected) {
      return;
    }
    if (connectedAtMountRef.current) {
      // Consumed here rather than left set, so a later drop and recovery still refetches.
      connectedAtMountRef.current = false;
      return;
    }
    callbackRef.current();
  }, [isConnected]);
}

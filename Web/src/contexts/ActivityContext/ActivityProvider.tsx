import React, { useEffect, useMemo, useRef, useState } from 'react';
import { useSignalR } from '@contexts/SignalRContext/useSignalR';
import type { ActivitySnapshotEvent } from '@contexts/SignalRContext/types';
import { ActivityContext, type ActivityLookup } from './context';

// domain and aspect come from fixed string unions that never contain '/', so `${domain}/${aspect}` is
// a collision-free bucket key; the inner map is entity key -> active count.
type ActivityMap = Map<string, Map<string, number>>;

const buildMap = (event: ActivitySnapshotEvent): ActivityMap => {
  const map: ActivityMap = new Map();
  for (const item of event.activities) {
    if (!item.isActive) continue;
    const bucket = `${item.domain}/${item.aspect}`;
    let inner = map.get(bucket);
    if (!inner) {
      inner = new Map();
      map.set(bucket, inner);
    }
    inner.set(item.key, item.activeCount ?? 1);
  }
  return map;
};

interface ActivityProviderProps {
  children: React.ReactNode;
}

/**
 * Subscribes once to the unified `ActivityUpdated` snapshot and exposes it to every status dot via
 * useActivityStatus - so all "is this running/active now" indicators share a single event instead of
 * N per-domain channels. Rich per-domain channels (speed, last-seen, lifecycle) are unchanged.
 */
export const ActivityProvider: React.FC<ActivityProviderProps> = ({ children }) => {
  const { on, off, connectionState } = useSignalR();
  const [activities, setActivities] = useState<ActivityMap>(() => new Map());
  // Flips true once, the first time a snapshot is processed - lets a consumer distinguish "the registry
  // has no data yet" from "the registry says inactive" (see ActivityLookup.ready).
  const [ready, setReady] = useState(false);
  // Highest revision applied - guards against an out-of-order older snapshot overwriting a newer one.
  const revisionRef = useRef(-1);
  // Armed whenever the connection is not established (and initially), so the FIRST snapshot after
  // (re)connect - the hub's reseed - is accepted as the new baseline regardless of its revision. This
  // is set on DISCONNECT rather than via a post-connect reset, so a restarted server whose reseed
  // arrives before any effect runs is still accepted instead of being dropped as "stale".
  const resyncRef = useRef(true);

  useEffect(() => {
    const handleActivityUpdated = (event: ActivitySnapshotEvent) => {
      if (!event || typeof event.revision !== 'number' || !Array.isArray(event.activities)) {
        return;
      }
      if (resyncRef.current) {
        // First snapshot after (re)connect: take it as the new baseline.
        resyncRef.current = false;
      } else if (event.revision < revisionRef.current) {
        // Stale/reordered snapshot within a connection - the newest revision wins.
        return;
      }
      revisionRef.current = event.revision;
      setActivities(buildMap(event));
      setReady(true);
    };
    on('ActivityUpdated', handleActivityUpdated);
    return () => off('ActivityUpdated', handleActivityUpdated);
  }, [on, off]);

  // Arm the resync while the connection is down so the next reseed becomes the new baseline.
  useEffect(() => {
    if (connectionState !== 'connected') {
      resyncRef.current = true;
    }
  }, [connectionState]);

  const value = useMemo<ActivityLookup>(() => {
    const isActive: ActivityLookup['isActive'] = (domain, key, aspect) =>
      (activities.get(`${domain}/${aspect}`)?.get(key) ?? 0) > 0;

    return {
      isActive,
      activeCount: (domain, key, aspect) => activities.get(`${domain}/${aspect}`)?.get(key) ?? 0,
      ready,
      isActiveOrFallback: (domain, key, aspect, fallback) =>
        ready ? isActive(domain, key, aspect) : fallback
    };
  }, [activities, ready]);

  return <ActivityContext.Provider value={value}>{children}</ActivityContext.Provider>;
};

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

// Buckets (`${domain}/${aspect}`) whose active->inactive transition is held visible for at least
// MIN_HOLD_MS instead of clearing immediately. Scoped to just the schedules dot rather than applied
// app-wide: a schedule run can start and finish within a single render tick (see MIN_HOLD_MS below),
// which every other consumer of this context does not need protecting against.
const HELD_BUCKETS = new Set(['schedule/running']);
const MIN_HOLD_MS = 1500;

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

  // Mirrors the RAW reported state, as opposed to `activities` (which also carries held entries) - so
  // each new event is diffed against what the PREVIOUS event actually reported, not against whatever
  // has been rendered. React can batch two setActivities() calls from two events processed back to
  // back into a single render, silently dropping an intermediate true value a downstream min-duration
  // hook would otherwise catch - but it never skips or batches the event handler invocations
  // themselves, so diffing here, on every raw event, catches a start->finish transition regardless.
  const reportedRef = useRef<ActivityMap>(new Map());
  // Entries currently being held past their reported inactive moment, keyed by `${bucket} ${key}`.
  // Consulted (not just diffed-into) on EVERY event, not only the one where the drop was first
  // detected - an unrelated event arriving mid-hold still rebuilds `next` from that event's own
  // `incoming` snapshot, which by definition no longer has this key; re-attaching from this map is
  // what keeps the hold alive until its own timer fires, instead of being wiped out by the next event.
  const heldRef = useRef(
    new Map<
      string,
      { bucket: string; key: string; count: number; timer: ReturnType<typeof setTimeout> }
    >()
  );

  useEffect(() => {
    const clearAllHolds = () => {
      heldRef.current.forEach(({ timer }) => clearTimeout(timer));
      heldRef.current.clear();
    };

    const handleActivityUpdated = (event: ActivitySnapshotEvent) => {
      if (!event || typeof event.revision !== 'number' || !Array.isArray(event.activities)) {
        return;
      }

      const isResync = resyncRef.current;
      if (isResync) {
        // First snapshot after (re)connect: take it as the new baseline.
        resyncRef.current = false;
      } else if (event.revision < revisionRef.current) {
        // Stale/reordered snapshot within a connection - the newest revision wins.
        return;
      }
      revisionRef.current = event.revision;

      const incoming = buildMap(event);
      const previousReported = reportedRef.current;
      reportedRef.current = incoming;

      if (isResync) {
        // A reconnect reseed is a fresh live baseline - a hold pending from before the gap concerns a
        // transition that may no longer even be true, so drop it and adopt the reseed as-is.
        clearAllHolds();
        setActivities(incoming);
        setReady(true);
        return;
      }

      // Start from the fresh snapshot alone (full replace, matching every bucket's prior behavior) -
      // NOT a merge into the previous state, which would never clear a bucket this event doesn't
      // mention. Held entries (below) are re-attached explicitly; every other absence here means "no
      // longer active" and is dropped immediately, exactly as before this hold logic existed.
      const next = new Map<string, Map<string, number>>();
      for (const [bucket, innerIncoming] of incoming) {
        next.set(bucket, new Map(innerIncoming));
      }

      // Cancel the hold for anything this snapshot reports active again.
      for (const [timerKey, held] of heldRef.current) {
        if (incoming.get(held.bucket)?.has(held.key)) {
          clearTimeout(held.timer);
          heldRef.current.delete(timerKey);
        }
      }

      // Arm a new hold for anything a HELD bucket just reported as no longer active.
      for (const [bucket, innerPrev] of previousReported) {
        if (!HELD_BUCKETS.has(bucket)) {
          continue;
        }
        const innerIncoming = incoming.get(bucket);
        for (const [key, count] of innerPrev) {
          if (innerIncoming?.has(key)) {
            continue; // still active, no hold needed
          }
          const timerKey = `${bucket} ${key}`;
          if (heldRef.current.has(timerKey)) {
            continue; // already being held from an earlier drop
          }
          const timer = setTimeout(() => {
            heldRef.current.delete(timerKey);
            setActivities((prev) => {
              const inner = prev.get(bucket);
              if (!inner?.has(key)) {
                return prev;
              }
              const nextInner = new Map(inner);
              nextInner.delete(key);
              const nextMap = new Map(prev);
              if (nextInner.size === 0) {
                nextMap.delete(bucket);
              } else {
                nextMap.set(bucket, nextInner);
              }
              return nextMap;
            });
          }, MIN_HOLD_MS);
          heldRef.current.set(timerKey, { bucket, key, count, timer });
        }
      }

      // Re-attach every currently-held entry, whether it just started holding above or has been
      // holding since an earlier event - this is what survives an unrelated intervening update.
      for (const held of heldRef.current.values()) {
        const nextInner = next.get(held.bucket) ?? new Map<string, number>();
        nextInner.set(held.key, held.count);
        next.set(held.bucket, nextInner);
      }

      setActivities(next);
      setReady(true);
    };
    on('ActivityUpdated', handleActivityUpdated);
    return () => {
      off('ActivityUpdated', handleActivityUpdated);
      clearAllHolds();
    };
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

import { useCallback, useEffect, useRef, useState } from 'react';
import type { TFunction } from 'i18next';
import ApiService from '@services/api.service';
import { useSignalR } from '@contexts/SignalRContext/useSignalR';
import type {
  EpicMappingCompleteEvent,
  EpicMappingProgressEvent,
  EpicMappingStartedEvent,
  MappingStageContext
} from '@contexts/SignalRContext/types';
import { useReconnectRefetch } from './useReconnectRefetch';

/** `unknown` until the server said whether a mapping runs: a status read that failed decides nothing. */
export type EpicMappingPhase = 'unknown' | 'none' | 'running' | 'done' | 'failed' | 'canceled';

export interface EpicMappingRun {
  phase: EpicMappingPhase;
  /** 0-100, from the run's own progress events. */
  percent: number;
  /** The stage the run reported last; the browser words it, never the server. */
  stageKey: string | null;
  context: MappingStageContext | null;
}

interface EpicMappingRunState {
  run: EpicMappingRun;
  /** Forgets the run the form showed, so its ending is not read as the next attempt's. */
  clear: () => void;
}

const NO_RUN: EpicMappingRun = { phase: 'none', percent: 0, stageKey: null, context: null };

const endedRun = (event: EpicMappingCompleteEvent): EpicMappingRun => ({
  phase: event.canceled ? 'canceled' : event.success ? 'done' : 'failed',
  percent: event.percentComplete,
  stageKey: event.stageKey,
  context: event.context
});

/**
 * The Epic game mapping operation as the sign-in dialog and the setup step show it. It follows one operation by its
 * id: the one that starts while `armed` (from the moment a code is sent), or the one the schedule status names when
 * it is mounted while a mapping already runs. The server is busy before it stores the id (the code exchange, the
 * start of a scheduled refresh), so a busy status without one is read again once that mapping starts. After a
 * reconnect it reads how that operation stands, since an end event sent while the socket was down is not replayed.
 */
export function useEpicMappingRun(armed: boolean): EpicMappingRunState {
  const { on, off, isConnected } = useSignalR();
  const [run, setRun] = useState<EpicMappingRun>({ ...NO_RUN, phase: 'unknown' });
  const armedRef = useRef(armed);
  armedRef.current = armed;
  // The operation this run follows; null until one is named.
  const operationRef = useRef<string | null>(null);
  // An ending that arrived while nothing was followed, for the status read that then names that operation.
  const lastCompleteRef = useRef<EpicMappingCompleteEvent | null>(null);
  // The last status read found the server busy with no id yet, so the next mapping start is read for its id.
  const waitingRef = useRef(false);
  // A mapping started while a status read was out, so an answer still without an id is already stale.
  const startedDuringReadRef = useRef(false);

  const clear = useCallback(() => {
    operationRef.current = null;
    lastCompleteRef.current = null;
    waitingRef.current = false;
    startedDuringReadRef.current = false;
    // Nothing was ever read yet, so there is still nothing known to forget.
    setRun((existing) => (existing.phase === 'unknown' ? existing : NO_RUN));
  }, []);

  // A new code submission starts from a clean run, whose mapping is about to start.
  useEffect(() => {
    if (!armed) return;
    clear();
    setRun(NO_RUN);
  }, [armed, clear]);

  const readRun = useCallback(() => {
    const followed = operationRef.current;
    if (followed === null) {
      startedDuringReadRef.current = false;
      void ApiService.getEpicScheduleStatus()
        .then((status) => {
          if (operationRef.current !== null) return;
          const busyWithoutId = status.isProcessing && status.operationId === undefined;
          if (busyWithoutId && startedDuringReadRef.current) {
            readRun();
            return;
          }
          waitingRef.current = busyWithoutId;
          // A run is joined only by the id the server names; a busy flag without one (the code exchange) is idle.
          if (!status.isProcessing || status.operationId === undefined) {
            setRun((existing) => (existing.phase === 'unknown' ? NO_RUN : existing));
            return;
          }
          operationRef.current = status.operationId;
          const ended = lastCompleteRef.current;
          if (ended?.operationId === status.operationId) {
            setRun(endedRun(ended));
            return;
          }
          setRun((existing) =>
            existing.phase === 'unknown' || existing.phase === 'none'
              ? { phase: 'running', percent: status.progressPercent, stageKey: null, context: null }
              : existing
          );
        })
        .catch((error: unknown) => {
          console.warn('Epic mapping status unavailable:', error);
        });
      return;
    }
    const finish = (ending: Omit<EpicMappingRun, 'percent'>) => {
      setRun((existing) =>
        existing.phase === 'running' && operationRef.current === followed
          ? { ...ending, percent: existing.percent }
          : existing
      );
    };
    const completed: Omit<EpicMappingRun, 'percent'> = {
      phase: 'done',
      stageKey: 'signalr.epicMapping.completed',
      context: null
    };
    void ApiService.getTrackedOperation(followed)
      .then((answer) => {
        if (answer.active || operationRef.current !== followed) return;
        if (answer.status === 'completed') {
          // A logout or a refused token between the two reads resets the count to 0, so a count is shown only while
          // the account is still signed in; otherwise the ending says the refresh completed without one.
          void ApiService.getEpicMappingAuthStatus()
            .then((status) => {
              if (status.isAuthenticated) {
                finish({
                  phase: 'done',
                  stageKey: 'signalr.epicMapping.mappingDone',
                  context: { count: status.gamesDiscovered }
                });
                return;
              }
              finish(completed);
            })
            .catch((error: unknown) => {
              console.warn('Epic mapping account status unavailable:', error);
              finish(completed);
            });
          return;
        }
        // The server keeps an ended run's status for a few minutes; a run it no longer knows ended unseen.
        finish(
          answer.status === 'canceled'
            ? { phase: 'canceled', stageKey: 'signalr.epicMapping.canceled', context: null }
            : { phase: 'failed', stageKey: 'signalr.epicMapping.mappingFailed', context: null }
        );
      })
      .catch((error: unknown) => {
        console.warn('Epic mapping operation unavailable:', error);
      });
  }, []);

  useEffect(() => {
    readRun();
  }, [readRun]);

  useReconnectRefetch(isConnected, readRun);

  useEffect(() => {
    const follows = (operationId: string) => operationRef.current === operationId;
    const handleStarted = (event: EpicMappingStartedEvent) => {
      if (operationRef.current === null && armedRef.current)
        operationRef.current = event.operationId;
      if (operationRef.current === null) {
        startedDuringReadRef.current = true;
        if (waitingRef.current) readRun();
        return;
      }
      if (!follows(event.operationId)) return;
      setRun({ phase: 'running', percent: 0, stageKey: event.stageKey, context: event.context });
    };
    const handleProgress = (event: EpicMappingProgressEvent) => {
      if (!follows(event.operationId)) return;
      setRun({
        phase: 'running',
        percent: event.percentComplete,
        stageKey: event.stageKey,
        context: event.context
      });
    };
    const handleComplete = (event: EpicMappingCompleteEvent) => {
      if (!follows(event.operationId)) {
        if (operationRef.current === null) lastCompleteRef.current = event;
        return;
      }
      setRun(endedRun(event));
    };
    on('EpicMappingStarted', handleStarted);
    on('EpicMappingProgress', handleProgress);
    on('EpicMappingComplete', handleComplete);
    return () => {
      off('EpicMappingStarted', handleStarted);
      off('EpicMappingProgress', handleProgress);
      off('EpicMappingComplete', handleComplete);
    };
  }, [on, off, readRun]);

  return { run, clear };
}

/** The line that tells where the mapping stands, worded from the stage key it last reported. */
export function describeEpicMappingRun(run: EpicMappingRun, t: TFunction): string {
  if (run.phase === 'canceled') return t('signalr.epicMapping.canceled');
  if (run.stageKey === null) return t('signalr.epicMapping.codeAccepted');
  return t(run.stageKey, run.context ?? {});
}

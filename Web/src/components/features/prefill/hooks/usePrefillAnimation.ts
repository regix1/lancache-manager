import { useRef, useCallback } from 'react';
import { ANIMATION_DURATION_MS, ANIMATION_COMPLETION_DELAY_MS } from './prefillConstants';
import type { CachedAnimationItem, PrefillProgress } from './prefillTypes';

interface UsePrefillAnimationReturn {
  currentAnimationAppIdRef: React.RefObject<string>;
  cachedAnimationQueueRef: React.RefObject<CachedAnimationItem[]>;
  isProcessingAnimationRef: React.RefObject<boolean>;
  resetAnimationState: () => void;
  /**
   * Hard-stop: clears the queue AND cancels any in-flight requestAnimationFrame / pending
   * completion timeout so a cancelled prefill can never keep painting the bar. Used by the
   * Cancel path (animation-queue race, diagnostic I7).
   */
  stopAnimations: () => void;
  enqueueAnimation: (
    item: CachedAnimationItem,
    setPrefillProgress: React.Dispatch<React.SetStateAction<PrefillProgress | null>>
  ) => void;
}

/**
 * Hook for managing the animation queue for cached games in prefill
 * This handles the sequential animation of progress updates for already-cached games
 */
export function usePrefillAnimation(): UsePrefillAnimationReturn {
  const cachedAnimationCountRef = useRef(0);
  const currentAnimationAppIdRef = useRef('');
  const cachedAnimationQueueRef = useRef<CachedAnimationItem[]>([]);
  const isProcessingAnimationRef = useRef(false);
  // Tracks the in-flight rAF + completion timeout so they can be cancelled on hard-stop.
  const rafIdRef = useRef<number | null>(null);
  const completionTimeoutRef = useRef<ReturnType<typeof setTimeout> | null>(null);
  const stoppedRef = useRef(false);
  const processAnimationQueueRef = useRef<
    | ((setPrefillProgress: React.Dispatch<React.SetStateAction<PrefillProgress | null>>) => void)
    | null
  >(null);

  const resetAnimationState = useCallback(() => {
    cachedAnimationQueueRef.current = [];
    isProcessingAnimationRef.current = false;
    currentAnimationAppIdRef.current = '';
    cachedAnimationCountRef.current = 0;
    stoppedRef.current = false;
    if (rafIdRef.current !== null) {
      cancelAnimationFrame(rafIdRef.current);
      rafIdRef.current = null;
    }
    if (completionTimeoutRef.current !== null) {
      clearTimeout(completionTimeoutRef.current);
      completionTimeoutRef.current = null;
    }
  }, []);

  const stopAnimations = useCallback(() => {
    stoppedRef.current = true;
    cachedAnimationQueueRef.current = [];
    isProcessingAnimationRef.current = false;
    currentAnimationAppIdRef.current = '';
    cachedAnimationCountRef.current = 0;
    if (rafIdRef.current !== null) {
      cancelAnimationFrame(rafIdRef.current);
      rafIdRef.current = null;
    }
    if (completionTimeoutRef.current !== null) {
      clearTimeout(completionTimeoutRef.current);
      completionTimeoutRef.current = null;
    }
  }, []);

  const processAnimationQueue = useCallback(
    (setPrefillProgress: React.Dispatch<React.SetStateAction<PrefillProgress | null>>) => {
      if (
        stoppedRef.current ||
        isProcessingAnimationRef.current ||
        cachedAnimationQueueRef.current.length === 0
      )
        return;

      const item = cachedAnimationQueueRef.current.shift();
      if (!item) return;

      isProcessingAnimationRef.current = true;
      currentAnimationAppIdRef.current = item.appId;

      const startTime = Date.now();

      const animateProgress = () => {
        // Bail immediately if a hard-stop (cancel) happened mid-frame.
        if (stoppedRef.current) {
          rafIdRef.current = null;
          return;
        }
        const elapsed = Date.now() - startTime;
        const percent = Math.min(100, (elapsed / ANIMATION_DURATION_MS) * 100);

        setPrefillProgress({
          state: 'already_cached',
          currentAppId: item.appId,
          currentAppName: item.appName,
          percentComplete: percent,
          bytesDownloaded: Math.floor((percent / 100) * item.totalBytes),
          totalBytes: item.totalBytes,
          bytesPerSecond: 0,
          elapsedSeconds: 0,
          // V11: carry the enqueue-time running counts so the two-tier overall bar keeps advancing
          // through cached-game animations instead of resetting to "Game 1 of N".
          expectedAppCount: item.expectedAppCount,
          updatedApps: item.updatedApps,
          alreadyUpToDate: item.alreadyUpToDate
        });

        if (elapsed < ANIMATION_DURATION_MS) {
          rafIdRef.current = requestAnimationFrame(animateProgress);
        } else {
          rafIdRef.current = null;
          completionTimeoutRef.current = setTimeout(() => {
            completionTimeoutRef.current = null;
            if (stoppedRef.current) return;
            cachedAnimationCountRef.current--;
            isProcessingAnimationRef.current = false;
            currentAnimationAppIdRef.current = '';
            // If queue is empty, clear progress (prefill may have already completed)
            if (cachedAnimationQueueRef.current.length === 0) {
              setPrefillProgress(null);
            } else {
              processAnimationQueueRef.current?.(setPrefillProgress);
            }
          }, ANIMATION_COMPLETION_DELAY_MS);
        }
      };

      animateProgress();
    },
    []
  );
  processAnimationQueueRef.current = processAnimationQueue;

  const enqueueAnimation = useCallback(
    (
      item: CachedAnimationItem,
      setPrefillProgress: React.Dispatch<React.SetStateAction<PrefillProgress | null>>
    ) => {
      // A fresh item means the queue is live again; clear any prior hard-stop latch.
      stoppedRef.current = false;
      cachedAnimationCountRef.current++;
      cachedAnimationQueueRef.current.push(item);
      processAnimationQueue(setPrefillProgress);
    },
    [processAnimationQueue]
  );

  return {
    currentAnimationAppIdRef,
    cachedAnimationQueueRef,
    isProcessingAnimationRef,
    resetAnimationState,
    stopAnimations,
    enqueueAnimation
  };
}

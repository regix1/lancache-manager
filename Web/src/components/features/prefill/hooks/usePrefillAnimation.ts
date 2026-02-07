import { useRef, useCallback } from 'react';
import { ANIMATION_DURATION_MS, ANIMATION_COMPLETION_DELAY_MS } from './prefillConstants';

interface CachedAnimationItem {
  appId: number;
  appName?: string;
  totalBytes: number;
}

interface PrefillProgress {
  state: string;
  message?: string;
  currentAppId: number;
  currentAppName?: string;
  percentComplete: number;
  bytesDownloaded: number;
  totalBytes: number;
  bytesPerSecond: number;
  elapsedSeconds: number;
}

interface UsePrefillAnimationReturn {
  cachedAnimationCountRef: React.RefObject<number>;
  currentAnimationAppIdRef: React.RefObject<number>;
  cachedAnimationQueueRef: React.RefObject<CachedAnimationItem[]>;
  isProcessingAnimationRef: React.RefObject<boolean>;
  resetAnimationState: () => void;
  processAnimationQueue: (setPrefillProgress: React.Dispatch<React.SetStateAction<PrefillProgress | null>>) => void;
  enqueueAnimation: (item: CachedAnimationItem, setPrefillProgress: React.Dispatch<React.SetStateAction<PrefillProgress | null>>) => void;
}

/**
 * Hook for managing the animation queue for cached games in prefill
 * This handles the sequential animation of progress updates for already-cached games
 */
export function usePrefillAnimation(): UsePrefillAnimationReturn {
  const cachedAnimationCountRef = useRef(0);
  const currentAnimationAppIdRef = useRef(0);
  const cachedAnimationQueueRef = useRef<CachedAnimationItem[]>([]);
  const isProcessingAnimationRef = useRef(false);

  const resetAnimationState = useCallback(() => {
    cachedAnimationQueueRef.current = [];
    isProcessingAnimationRef.current = false;
    currentAnimationAppIdRef.current = 0;
    cachedAnimationCountRef.current = 0;
  }, []);

  const processAnimationQueue = useCallback((setPrefillProgress: React.Dispatch<React.SetStateAction<PrefillProgress | null>>) => {
    if (
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
        elapsedSeconds: 0
      });

      if (elapsed < ANIMATION_DURATION_MS) {
        requestAnimationFrame(animateProgress);
      } else {
        setTimeout(() => {
          cachedAnimationCountRef.current--;
          isProcessingAnimationRef.current = false;
          currentAnimationAppIdRef.current = 0;
          // If queue is empty, clear progress (prefill may have already completed)
          if (cachedAnimationQueueRef.current.length === 0) {
            setPrefillProgress(null);
          } else {
            processAnimationQueue(setPrefillProgress);
          }
        }, ANIMATION_COMPLETION_DELAY_MS);
      }
    };

    animateProgress();
  }, []);

  const enqueueAnimation = useCallback((
    item: CachedAnimationItem,
    setPrefillProgress: React.Dispatch<React.SetStateAction<PrefillProgress | null>>
  ) => {
    cachedAnimationCountRef.current++;
    cachedAnimationQueueRef.current.push(item);
    processAnimationQueue(setPrefillProgress);
  }, [processAnimationQueue]);

  return {
    cachedAnimationCountRef,
    currentAnimationAppIdRef,
    cachedAnimationQueueRef,
    isProcessingAnimationRef,
    resetAnimationState,
    processAnimationQueue,
    enqueueAnimation
  };
}

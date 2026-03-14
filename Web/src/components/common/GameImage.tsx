import React, { useState, useCallback, useEffect, useContext, createContext } from 'react';

const API_BASE = import.meta.env.VITE_API_URL || '/api';

/**
 * Context for cache-busting game images after a manual refresh.
 * When the value changes, all GameImage URLs get a new query param,
 * forcing the browser to bypass its HTTP cache.
 */
export const ImageCacheContext = createContext(0);

/**
 * Context providing a callback to invalidate (bust) the image cache.
 * Call this after new game detection results are loaded to force
 * GameImage components to re-fetch images that may now be available.
 */
export const ImageCacheInvalidateContext = createContext<(() => void) | null>(null);

/**
 * Hook to obtain the image cache invalidation function.
 * Returns null when used outside of an ImageCacheInvalidateContext provider.
 */
export const useInvalidateImageCache = (): (() => void) | null =>
  useContext(ImageCacheInvalidateContext);

interface GameImageProps {
  gameAppId: string | number;
  alt: string;
  className?: string;
  loading?: 'lazy' | 'eager';
  onFinalError: (gameAppId: string) => void;
  sizes?: string;
  epicAppId?: string;
}

/**
 * Game image component with automatic fallback to capsule image when header fails.
 * Steam: /api/game-images/{appId}/header -> ?type=capsule -> placeholder
 * Epic: /api/game-images/epic/{epicAppId}/header -> placeholder
 */
export const GameImage: React.FC<GameImageProps> = ({
  gameAppId,
  alt,
  className,
  loading = 'lazy',
  onFinalError,
  sizes,
  epicAppId
}) => {
  const appId = String(gameAppId);
  const imageKey = epicAppId ? `epic-${epicAppId}` : appId;
  const [useCapsule, setUseCapsule] = useState(false);
  const [hasTriedFallback, setHasTriedFallback] = useState(false);
  const [epicRetryCount, setEpicRetryCount] = useState(0);
  const [retryTrigger, setRetryTrigger] = useState(0);
  const cacheBuster = useContext(ImageCacheContext);

  // Reset state when gameAppId/epicAppId changes
  useEffect(() => {
    setUseCapsule(false);
    setHasTriedFallback(false);
    setEpicRetryCount(0);
    setRetryTrigger(0);
  }, [imageKey]);

  const handleError = useCallback(() => {
    if (epicAppId) {
      // Epic: retry up to 2 times with increasing delays (3s, 6s)
      // This handles the race where auto-reconnect hasn't populated URLs yet
      if (epicRetryCount < 2) {
        const delay = (epicRetryCount + 1) * 3000;
        setTimeout(() => {
          setEpicRetryCount((c) => c + 1);
          setRetryTrigger((t) => t + 1);
        }, delay);
      } else {
        onFinalError(imageKey);
      }
    } else if (!useCapsule && !hasTriedFallback) {
      // Steam: first failure - try capsule image as fallback
      setUseCapsule(true);
      setHasTriedFallback(true);
    } else {
      // Capsule also failed: notify parent to show placeholder
      onFinalError(imageKey);
    }
  }, [epicAppId, useCapsule, hasTriedFallback, imageKey, onFinalError, epicRetryCount]);

  // Build cache-bust suffix
  const cbParam = cacheBuster > 0 ? `_cb=${cacheBuster}` : '';
  // Add retry trigger to force re-fetch on retry
  const retryParam = retryTrigger > 0 ? `_rt=${retryTrigger}` : '';
  const extraParams = [cbParam, retryParam].filter(Boolean).join('&');

  let src: string;
  if (epicAppId) {
    src = `${API_BASE}/game-images/epic/${epicAppId}/header${extraParams ? `?${extraParams}` : ''}`;
  } else if (useCapsule) {
    src = `${API_BASE}/game-images/${appId}/header?type=capsule${extraParams ? `&${extraParams}` : ''}`;
  } else {
    src = `${API_BASE}/game-images/${appId}/header${extraParams ? `?${extraParams}` : ''}`;
  }

  return (
    <img
      src={src}
      sizes={sizes}
      alt={alt}
      className={className}
      loading={loading}
      onError={handleError}
    />
  );
};

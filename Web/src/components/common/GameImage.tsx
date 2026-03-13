import React, { useState, useCallback, useEffect, useContext, createContext } from 'react';

const API_BASE = import.meta.env.VITE_API_URL || '/api';

/**
 * Context for cache-busting game images after a manual refresh.
 * When the value changes, all GameImage URLs get a new query param,
 * forcing the browser to bypass its HTTP cache.
 */
export const ImageCacheContext = createContext(0);

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
  const cacheBuster = useContext(ImageCacheContext);

  // Reset state when gameAppId/epicAppId changes (component reused for different game)
  useEffect(() => {
    setUseCapsule(false);
    setHasTriedFallback(false);
  }, [imageKey]);

  const handleError = useCallback(() => {
    console.log(
      `[GameImage] Image load error: imageKey=${imageKey}, epicAppId=${epicAppId ?? 'none'}, useCapsule=${useCapsule}`
    );
    if (epicAppId) {
      // Epic has no capsule fallback - go straight to placeholder
      onFinalError(imageKey);
    } else if (!useCapsule && !hasTriedFallback) {
      // Steam: first failure - try capsule image as fallback
      setUseCapsule(true);
      setHasTriedFallback(true);
    } else {
      // Capsule also failed: notify parent to show placeholder
      onFinalError(imageKey);
    }
  }, [epicAppId, useCapsule, hasTriedFallback, imageKey, onFinalError]);

  // Build cache-bust suffix (only when version > 0, i.e. after a manual refresh)
  const cbParam = cacheBuster > 0 ? `_cb=${cacheBuster}` : '';

  let src: string;
  if (epicAppId) {
    src = `${API_BASE}/game-images/epic/${epicAppId}/header${cbParam ? `?${cbParam}` : ''}`;
  } else if (useCapsule) {
    src = `${API_BASE}/game-images/${appId}/header?type=capsule${cbParam ? `&${cbParam}` : ''}`;
  } else {
    src = `${API_BASE}/game-images/${appId}/header${cbParam ? `?${cbParam}` : ''}`;
  }

  // Diagnostic logging for image sources
  if (epicAppId) {
    console.log(`[GameImage] Epic image request: epicAppId=${epicAppId}, src=${src}`);
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

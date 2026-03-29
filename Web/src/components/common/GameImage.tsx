import React, { useState, useCallback, useEffect, useContext } from 'react';
import { ImageCacheContext } from './ImageCacheContext';

const API_BASE = import.meta.env.VITE_API_URL || '/api';

interface GameImageProps {
  gameAppId: string | number;
  alt: string;
  className?: string;
  loading?: 'lazy' | 'eager';
  onFinalError: (gameAppId: string) => void;
  sizes?: string;
  epicAppId?: string;
  /** When set (e.g. from Download.gameImageUrl), load this URL first; on failure use /api/game-images proxy. */
  storedImageUrl?: string;
}

/**
 * Game image: optional stored CDN URL from DB, then /api/game-images proxy with header→capsule (Steam) or Epic retries.
 */
export const GameImage: React.FC<GameImageProps> = ({
  gameAppId,
  alt,
  className,
  loading = 'lazy',
  onFinalError,
  sizes,
  epicAppId,
  storedImageUrl
}) => {
  const appId = String(gameAppId);
  const imageKey = epicAppId ? `epic-${epicAppId}` : appId;
  const trimmedStored = storedImageUrl?.trim();
  const [storedBannerFailed, setStoredBannerFailed] = useState(false);
  const [useCapsule, setUseCapsule] = useState(false);
  const [hasTriedFallback, setHasTriedFallback] = useState(false);
  const [epicRetryCount, setEpicRetryCount] = useState(0);
  const [retryTrigger, setRetryTrigger] = useState(0);
  const cacheBuster = useContext(ImageCacheContext);

  useEffect(() => {
    setStoredBannerFailed(false);
    setUseCapsule(false);
    setHasTriedFallback(false);
    setEpicRetryCount(0);
    setRetryTrigger(0);
  }, [imageKey, trimmedStored]);

  const handleError = useCallback(() => {
    if (epicAppId) {
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
      setUseCapsule(true);
      setHasTriedFallback(true);
    } else {
      onFinalError(imageKey);
    }
  }, [epicAppId, useCapsule, hasTriedFallback, imageKey, onFinalError, epicRetryCount]);

  const cbParam = cacheBuster > 0 ? `_cb=${cacheBuster}` : '';
  const retryParam = retryTrigger > 0 ? `_rt=${retryTrigger}` : '';
  const extraParams = [cbParam, retryParam].filter(Boolean).join('&');

  if (trimmedStored && !storedBannerFailed) {
    return (
      <img
        src={trimmedStored}
        sizes={sizes}
        alt={alt}
        className={className}
        loading={loading}
        onError={() => setStoredBannerFailed(true)}
      />
    );
  }

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

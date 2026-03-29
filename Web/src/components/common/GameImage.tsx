import React, { useState, useMemo, useEffect, useContext } from 'react';
import { ImageCacheContext } from './ImageCacheContext';

const API_BASE = import.meta.env.VITE_API_URL || '/api';

interface GameImageProps {
  gameAppId?: string | number;
  alt: string;
  className?: string;
  loading?: 'lazy' | 'eager';
  onError: (gameAppId: string) => void;
  sizes?: string;
  epicAppId?: string;
  /** When set (e.g. from Download.gameImageUrl), load this URL directly. */
  imageUrl?: string;
}

/**
 * Game image: single URL attempt — imageUrl if provided, otherwise /api/game-images proxy.
 */
export const GameImage: React.FC<GameImageProps> = ({
  gameAppId,
  alt,
  className,
  loading = 'lazy',
  onError,
  sizes,
  epicAppId,
  imageUrl
}) => {
  const appId = gameAppId != null ? String(gameAppId) : '';
  const imageKey = epicAppId ? `epic-${epicAppId}` : appId;
  const [failed, setFailed] = useState(false);
  const cacheBuster = useContext(ImageCacheContext);

  useEffect(() => {
    setFailed(false);
  }, [imageKey, imageUrl]);

  const src = useMemo(() => {
    if (imageUrl?.trim()) return imageUrl.trim();
    if (epicAppId) return `${API_BASE}/game-images/epic/${epicAppId}/header`;
    if (appId) return `${API_BASE}/game-images/${appId}/header`;
    return null;
  }, [imageUrl, epicAppId, appId]);

  const finalSrc =
    src && cacheBuster > 0
      ? `${src}${src.includes('?') ? '&' : '?'}_cb=${cacheBuster}`
      : (src ?? undefined);

  useEffect(() => {
    if (failed || !src) {
      onError(imageKey);
    }
  }, [failed, src, imageKey, onError]);

  if (failed || !src) return null;

  return (
    <img
      src={finalSrc}
      sizes={sizes}
      alt={alt}
      className={className}
      loading={loading}
      onError={() => setFailed(true)}
    />
  );
};

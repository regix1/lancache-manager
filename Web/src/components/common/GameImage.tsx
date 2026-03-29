import React, { useState, useMemo, useEffect, useContext } from 'react';
import { ImageCacheContext } from './ImageCacheContext';

const API_BASE = import.meta.env.VITE_API_URL || '/api';

interface GameImageProps {
  gameAppId: string | number;
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
  const appId = String(gameAppId);
  const imageKey = epicAppId ? `epic-${epicAppId}` : appId;
  const [failed, setFailed] = useState(false);
  const cacheBuster = useContext(ImageCacheContext);

  useEffect(() => {
    setFailed(false);
  }, [imageKey, imageUrl]);

  const src = useMemo(() => {
    if (imageUrl?.trim()) return imageUrl.trim();
    if (epicAppId) return `${API_BASE}/game-images/epic/${epicAppId}/header`;
    return `${API_BASE}/game-images/${appId}/header`;
  }, [imageUrl, epicAppId, appId]);

  const finalSrc =
    cacheBuster > 0 ? `${src}${src.includes('?') ? '&' : '?'}_cb=${cacheBuster}` : src;

  useEffect(() => {
    if (failed) {
      onError(imageKey);
    }
  }, [failed, imageKey, onError]);

  if (failed) return null;

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

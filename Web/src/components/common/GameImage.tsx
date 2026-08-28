import React, { useState, useMemo, useEffect } from 'react';
import { useAvailableGameImages } from '@hooks/useAvailableGameImages';

const API_BASE = import.meta.env.VITE_API_URL || '/api';

interface GameImageProps {
  gameAppId?: string | number;
  alt: string;
  className?: string;
  loading?: 'lazy' | 'eager';
  onError: (gameAppId: string) => void;
  sizes?: string;
  epicAppId?: string;
  /** Canonical name-keyed service ("blizzard" | "riot" | "xbox") for games identified only by GameName. */
  nameKeyedService?: string;
  /** Normalized GameName slug, paired with nameKeyedService. */
  nameKeyedSlug?: string;
}

/**
 * Game image: always loads from /api/game-images proxy.
 */
export const GameImage: React.FC<GameImageProps> = ({
  gameAppId,
  alt,
  className,
  loading = 'lazy',
  onError,
  sizes,
  epicAppId,
  nameKeyedService,
  nameKeyedSlug
}) => {
  const appId = gameAppId != null ? String(gameAppId) : '';
  const isNameKeyed = Boolean(nameKeyedService && nameKeyedSlug);
  const imageKey = isNameKeyed
    ? `${nameKeyedService}-${nameKeyedSlug}`
    : epicAppId
      ? `epic-${epicAppId}`
      : appId;
  const [failed, setFailed] = useState(false);

  // The same id the row already looked up to decide whether to render this banner at all, which is
  // what /available reports a version for. The version is therefore known at first paint, so the
  // element is never given one URL and then handed another.
  const availableKey = (isNameKeyed ? nameKeyedSlug : epicAppId) ?? appId;
  const version = useAvailableGameImages().versionOf(availableKey);

  useEffect(() => {
    setFailed(false);
  }, [imageKey, version]);

  // The version is part of the path, so replaced artwork is a different resource the browser has to
  // go and get, while an unchanged banner keeps the URL it already has cached. A caching proxy sits
  // between this and the server on a LAN box by definition, and a path segment is opaque to one
  // where a query string is what such a layer refuses or normalizes away.
  const src = useMemo(() => {
    if (isNameKeyed)
      return `${API_BASE}/game-images/name/${nameKeyedService}/${nameKeyedSlug}/header/${version}`;
    if (epicAppId) return `${API_BASE}/game-images/epic/${epicAppId}/header/${version}`;
    if (appId) return `${API_BASE}/game-images/${appId}/header/${version}`;
    return null;
  }, [isNameKeyed, nameKeyedService, nameKeyedSlug, epicAppId, appId, version]);

  useEffect(() => {
    if (failed || !src) {
      onError(imageKey);
    }
  }, [failed, src, imageKey, onError]);

  if (failed || !src) return null;

  return (
    <img
      src={src}
      sizes={sizes}
      alt={alt}
      className={className}
      loading={loading}
      onError={() => setFailed(true)}
    />
  );
};

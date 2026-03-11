import React, { useState, useCallback, useEffect } from 'react';

const API_BASE = import.meta.env.VITE_API_URL || '/api';

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

  // Reset state when gameAppId/epicAppId changes (component reused for different game)
  useEffect(() => {
    setUseCapsule(false);
    setHasTriedFallback(false);
  }, [imageKey]);

  const handleError = useCallback(() => {
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

  let src: string;
  if (epicAppId) {
    src = `${API_BASE}/game-images/epic/${epicAppId}/header`;
  } else if (useCapsule) {
    src = `${API_BASE}/game-images/${appId}/header?type=capsule`;
  } else {
    src = `${API_BASE}/game-images/${appId}/header`;
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

import React, { useState, useCallback } from 'react';

const API_BASE = import.meta.env.VITE_API_URL || '/api';

interface GameImageProps {
  gameAppId: string | number;
  alt: string;
  className?: string;
  loading?: 'lazy' | 'eager';
  onFinalError: (gameAppId: string) => void;
  sizes?: string;
}

/**
 * Game image component with automatic fallback to capsule image when header fails.
 * Backend endpoint: /api/game-images/{appId}/header?type=capsule
 * Falls back through: header -> capsule (via query param) -> error handler (shows placeholder)
 */
export const GameImage: React.FC<GameImageProps> = ({
  gameAppId,
  alt,
  className,
  loading = 'lazy',
  onFinalError,
  sizes
}) => {
  const appId = String(gameAppId);
  const [useCapsule, setUseCapsule] = useState(false);
  const [hasTriedFallback, setHasTriedFallback] = useState(false);

  const handleError = useCallback(() => {
    if (!useCapsule && !hasTriedFallback) {
      // First failure: try capsule image as fallback (via query parameter)
      setUseCapsule(true);
      setHasTriedFallback(true);
    } else {
      // Capsule also failed: notify parent to show placeholder
      onFinalError(appId);
    }
  }, [useCapsule, hasTriedFallback, appId, onFinalError]);

  // Backend uses /header endpoint with optional ?type=capsule query param
  const src = useCapsule
    ? `${API_BASE}/game-images/${appId}/header?type=capsule`
    : `${API_BASE}/game-images/${appId}/header`;
  
  // Only use srcSet for initial header load (includes both resolutions)
  const srcSet = !useCapsule
    ? `${API_BASE}/game-images/${appId}/header?type=capsule 616w, ${API_BASE}/game-images/${appId}/header 460w`
    : undefined;

  return (
    <img
      src={src}
      srcSet={srcSet}
      sizes={sizes}
      alt={alt}
      className={className}
      loading={loading}
      onError={handleError}
    />
  );
};

export default GameImage;

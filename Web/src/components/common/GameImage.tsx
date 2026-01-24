import React, { useState, useCallback } from 'react';

const API_BASE = import.meta.env.VITE_API_URL || '';

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
 * Falls back through: header -> capsule -> error handler (shows placeholder)
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
  const [imageType, setImageType] = useState<'header' | 'capsule'>('header');
  const [hasTriedFallback, setHasTriedFallback] = useState(false);

  const handleError = useCallback(() => {
    if (imageType === 'header' && !hasTriedFallback) {
      // First failure: try capsule image as fallback
      setImageType('capsule');
      setHasTriedFallback(true);
    } else {
      // Capsule also failed: notify parent to show placeholder
      onFinalError(appId);
    }
  }, [imageType, hasTriedFallback, appId, onFinalError]);

  const src = `${API_BASE}/game-images/${appId}/${imageType}`;
  
  // Only use srcSet for header images (capsule is already the fallback)
  const srcSet = imageType === 'header' 
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

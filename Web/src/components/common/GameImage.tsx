import React, { useState, useMemo, useEffect, useContext, useLayoutEffect, useRef } from 'react';
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
  /** Forces img remount when display profile changes (e.g. smooth vs crisp banners). */
  renderProfileKey?: string;
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
  renderProfileKey
}) => {
  const appId = gameAppId != null ? String(gameAppId) : '';
  const imageKey = epicAppId ? `epic-${epicAppId}` : appId;
  const [failed, setFailed] = useState(false);
  const cacheBuster = useContext(ImageCacheContext);

  useEffect(() => {
    setFailed(false);
  }, [imageKey]);

  const src = useMemo(() => {
    if (epicAppId) return `${API_BASE}/game-images/epic/${epicAppId}/header`;
    if (appId) return `${API_BASE}/game-images/${appId}/header`;
    return null;
  }, [epicAppId, appId]);

  const profileToken = renderProfileKey ?? 'default';

  const finalSrc = useMemo(() => {
    if (!src) return undefined;
    const params = new URLSearchParams();
    if (cacheBuster > 0) params.set('_cb', String(cacheBuster));
    params.set('_rp', profileToken);
    const qs = params.toString();
    return qs ? `${src}?${qs}` : src;
  }, [src, cacheBuster, profileToken]);

  // Browsers cache decoded bitmaps; remount when render profile changes so CSS scaling updates apply.
  const [visible, setVisible] = useState(true);
  const skipProfileRemountRef = useRef(true);
  useLayoutEffect(() => {
    if (skipProfileRemountRef.current) {
      skipProfileRemountRef.current = false;
      return;
    }
    setVisible(false);
    const frame = requestAnimationFrame(() => setVisible(true));
    return () => cancelAnimationFrame(frame);
  }, [profileToken]);

  useEffect(() => {
    if (failed || !src) {
      onError(imageKey);
    }
  }, [failed, src, imageKey, onError]);

  if (failed || !src || !visible) return null;

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

import React, { useState, useMemo, useEffect, useContext, useRef, useCallback } from 'react';
import { ImageCacheContext } from './ImageCacheContext';
import {
  getCrispBannerCacheKey,
  readCachedCrispBanner,
  renderCrispBannerImage,
  writeCachedCrispBanner,
  type BannerScalingMode
} from '@utils/bannerImageScaling';

const API_BASE = import.meta.env.VITE_API_URL || '/api';

interface GameImageProps {
  gameAppId?: string | number;
  alt: string;
  className?: string;
  loading?: 'lazy' | 'eager';
  onError: (gameAppId: string) => void;
  sizes?: string;
  epicAppId?: string;
  /** When set, crisp mode pre-scales via canvas to the displayed pixel size. */
  scalingMode?: BannerScalingMode;
}

/**
 * Game image: always loads from /api/game-images proxy.
 */
export const GameImage: React.FC<GameImageProps> = ({
  gameAppId,
  alt,
  className = '',
  loading = 'lazy',
  onError,
  sizes,
  epicAppId,
  scalingMode
}) => {
  const appId = gameAppId != null ? String(gameAppId) : '';
  const imageKey = epicAppId ? `epic-${epicAppId}` : appId;
  const [failed, setFailed] = useState(false);
  const [crispSrc, setCrispSrc] = useState<string | null>(null);
  const [sourceReady, setSourceReady] = useState(false);
  const cacheBuster = useContext(ImageCacheContext);
  const imgRef = useRef<HTMLImageElement>(null);
  const sourceImageRef = useRef<HTMLImageElement | null>(null);

  useEffect(() => {
    setFailed(false);
    setCrispSrc(null);
    setSourceReady(false);
    sourceImageRef.current = null;
  }, [imageKey]);

  const src = useMemo(() => {
    if (epicAppId) return `${API_BASE}/game-images/epic/${epicAppId}/header`;
    if (appId) return `${API_BASE}/game-images/${appId}/header`;
    return null;
  }, [epicAppId, appId]);

  const finalSrc =
    src && cacheBuster > 0
      ? `${src}${src.includes('?') ? '&' : '?'}_cb=${cacheBuster}`
      : (src ?? undefined);

  useEffect(() => {
    if (failed || !src) {
      onError(imageKey);
    }
  }, [failed, src, imageKey, onError]);

  useEffect(() => {
    setCrispSrc(null);
    setSourceReady(false);
    sourceImageRef.current = null;
  }, [finalSrc, scalingMode]);

  useEffect(() => {
    if (scalingMode !== 'crisp' || !finalSrc || failed) {
      setSourceReady(false);
      sourceImageRef.current = null;
      return;
    }

    let cancelled = false;
    const loader = new Image();
    loader.crossOrigin = 'anonymous';
    loader.onload = () => {
      if (cancelled) return;
      sourceImageRef.current = loader;
      setSourceReady(true);
    };
    loader.onerror = () => {
      if (!cancelled) setFailed(true);
    };
    loader.src = finalSrc;

    return () => {
      cancelled = true;
    };
  }, [scalingMode, finalSrc, failed]);

  const updateCrispSrc = useCallback(() => {
    if (scalingMode !== 'crisp' || !finalSrc || failed) return;

    const element = imgRef.current;
    const sourceImage = sourceImageRef.current;
    if (!element || !sourceImage || sourceImage.naturalWidth === 0) return;

    const displayWidth = element.clientWidth;
    const displayHeight = element.clientHeight;
    if (displayWidth < 2 || displayHeight < 2) return;

    const cacheKey = getCrispBannerCacheKey(finalSrc, displayWidth, displayHeight);
    const cached = readCachedCrispBanner(cacheKey);
    if (cached) {
      setCrispSrc(cached);
      return;
    }

    const rendered = renderCrispBannerImage(
      sourceImage,
      sourceImage.naturalWidth,
      sourceImage.naturalHeight,
      displayWidth,
      displayHeight
    );
    if (!rendered) return;

    writeCachedCrispBanner(cacheKey, rendered);
    setCrispSrc(rendered);
  }, [scalingMode, finalSrc, failed]);

  useEffect(() => {
    if (scalingMode !== 'crisp' || !finalSrc || failed || !sourceReady) return;

    updateCrispSrc();
    const element = imgRef.current;
    if (!element) return;

    const observer = new ResizeObserver(() => updateCrispSrc());
    observer.observe(element);
    return () => observer.disconnect();
  }, [scalingMode, finalSrc, failed, sourceReady, updateCrispSrc]);

  if (failed || !src) return null;

  const useCrispOutput = scalingMode === 'crisp' && crispSrc != null;
  const displaySrc = useCrispOutput ? crispSrc : finalSrc;
  const displayClassName = useCrispOutput
    ? className.replace(/\S+--crisp\b/g, (token) => token.replace('--crisp', '--crisp-processed'))
    : className;

  return (
    <img
      ref={imgRef}
      src={displaySrc}
      sizes={sizes}
      alt={alt}
      className={displayClassName}
      loading={loading}
      onError={() => setFailed(true)}
    />
  );
};

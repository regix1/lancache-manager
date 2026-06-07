import React, { useState, useMemo, useEffect, useContext, useRef, useCallback } from 'react';
import { ImageCacheContext } from './ImageCacheContext';
import {
  getCrispBannerCacheKey,
  readCachedCrispBanner,
  renderCrispBannerImage,
  writeCachedCrispBanner,
  type BannerScalingMode
} from '@utils/bannerImageScaling';
import { logBannerImageDebug, warnBannerImageDebug } from '@utils/bannerImageDebug';

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
    logBannerImageDebug('game-image', 'Render state', {
      imageKey,
      scalingMode: scalingMode ?? 'none',
      finalSrc,
      cacheBuster,
      failed,
      sourceReady,
      hasCrispSrc: crispSrc != null
    });
  }, [imageKey, scalingMode, finalSrc, cacheBuster, failed, sourceReady, crispSrc]);

  useEffect(() => {
    if (failed || !src) {
      onError(imageKey);
    }
  }, [failed, src, imageKey, onError]);

  useEffect(() => {
    setCrispSrc(null);
    setSourceReady(false);
    sourceImageRef.current = null;
    logBannerImageDebug('game-image', 'Scaling inputs changed — reset crisp pipeline', {
      imageKey,
      scalingMode: scalingMode ?? 'none',
      finalSrc
    });
  }, [finalSrc, scalingMode, imageKey]);

  useEffect(() => {
    if (scalingMode !== 'crisp' || !finalSrc || failed) {
      setSourceReady(false);
      sourceImageRef.current = null;
      return;
    }

    let cancelled = false;
    const loader = new Image();
    loader.crossOrigin = 'anonymous';

    logBannerImageDebug('game-image', 'Loading crisp source image', {
      imageKey,
      finalSrc,
      crossOrigin: loader.crossOrigin
    });

    loader.onload = () => {
      if (cancelled) return;
      sourceImageRef.current = loader;
      setSourceReady(true);
      logBannerImageDebug('game-image', 'Crisp source loaded', {
        imageKey,
        naturalWidth: loader.naturalWidth,
        naturalHeight: loader.naturalHeight,
        currentSrc: loader.currentSrc
      });
    };
    loader.onerror = () => {
      if (cancelled) return;
      warnBannerImageDebug('game-image', 'Crisp source failed to load', {
        imageKey,
        finalSrc,
        crossOrigin: loader.crossOrigin
      });
      setFailed(true);
    };
    loader.src = finalSrc;

    return () => {
      cancelled = true;
    };
  }, [scalingMode, finalSrc, failed, imageKey]);

  const updateCrispSrc = useCallback(() => {
    if (scalingMode !== 'crisp' || !finalSrc || failed) return;

    const element = imgRef.current;
    const sourceImage = sourceImageRef.current;
    if (!element || !sourceImage || sourceImage.naturalWidth === 0) {
      warnBannerImageDebug('game-image', 'Crisp update skipped — display or source not ready', {
        imageKey,
        hasElement: Boolean(element),
        hasSourceImage: Boolean(sourceImage),
        naturalWidth: sourceImage?.naturalWidth ?? 0,
        clientWidth: element?.clientWidth ?? 0,
        clientHeight: element?.clientHeight ?? 0
      });
      return;
    }

    const displayWidth = element.clientWidth;
    const displayHeight = element.clientHeight;
    if (displayWidth < 2 || displayHeight < 2) {
      warnBannerImageDebug('game-image', 'Crisp update skipped — display box too small', {
        imageKey,
        displayWidth,
        displayHeight,
        className
      });
      return;
    }

    const cacheKey = getCrispBannerCacheKey(finalSrc, displayWidth, displayHeight);
    const cached = readCachedCrispBanner(cacheKey);
    if (cached) {
      logBannerImageDebug('game-image', 'Using cached crisp render', {
        imageKey,
        cacheKey,
        displayWidth,
        displayHeight
      });
      setCrispSrc(cached);
      return;
    }

    const rendered = renderCrispBannerImage(
      sourceImage,
      sourceImage.naturalWidth,
      sourceImage.naturalHeight,
      displayWidth,
      displayHeight,
      imageKey
    );
    if (!rendered) {
      warnBannerImageDebug('game-image', 'Crisp render returned null', {
        imageKey,
        cacheKey,
        displayWidth,
        displayHeight,
        sourceWidth: sourceImage.naturalWidth,
        sourceHeight: sourceImage.naturalHeight
      });
      return;
    }

    writeCachedCrispBanner(cacheKey, rendered);
    setCrispSrc(rendered);
    logBannerImageDebug('game-image', 'Crisp render applied to img', {
      imageKey,
      cacheKey,
      displayWidth,
      displayHeight
    });
  }, [scalingMode, finalSrc, failed, imageKey, className]);

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

  const handleDisplayLoad = () => {
    const element = imgRef.current;
    logBannerImageDebug('game-image', 'Display img loaded', {
      imageKey,
      scalingMode: scalingMode ?? 'none',
      useCrispOutput,
      srcType: useCrispOutput ? 'data-url' : 'api-url',
      displaySrc: useCrispOutput ? '[data-url]' : displaySrc,
      className: displayClassName,
      naturalWidth: element?.naturalWidth ?? 0,
      naturalHeight: element?.naturalHeight ?? 0,
      clientWidth: element?.clientWidth ?? 0,
      clientHeight: element?.clientHeight ?? 0
    });
  };

  const handleDisplayError = () => {
    warnBannerImageDebug('game-image', 'Display img failed to load', {
      imageKey,
      scalingMode: scalingMode ?? 'none',
      useCrispOutput,
      displaySrc: useCrispOutput ? '[data-url]' : displaySrc
    });
    setFailed(true);
  };

  return (
    <img
      ref={imgRef}
      src={displaySrc}
      sizes={sizes}
      alt={alt}
      className={displayClassName}
      loading={loading}
      onLoad={handleDisplayLoad}
      onError={handleDisplayError}
    />
  );
};

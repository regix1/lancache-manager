import React, { useState, useEffect } from 'react';
import { Gamepad2, Loader } from 'lucide-react';

interface ImageWithFallbackProps {
  src: string;
  fallback?: React.ReactNode;
  alt: string;
  className?: string;
  style?: React.CSSProperties;
  onLoad?: () => void;
  onError?: () => void;
}

// Helper to get Steam CDN fallback URL
const getSteamCdnUrl = (originalUrl: string): string | null => {
  const appIdMatch = originalUrl.match(/\/gameimages\/(\d+)\//);
  if (appIdMatch) {
    const appId = appIdMatch[1];
    return `https://cdn.akamai.steamstatic.com/steam/apps/${appId}/header.jpg`;
  }
  return null;
};

// Global request deduplication cache
const pendingImageRequests = new Map<string, Promise<string>>();
const imageCache = new Map<string, { url: string; timestamp: number; success: boolean }>();
const CACHE_DURATION = 5 * 60 * 1000; // 5 minutes

// Helper to get cached or deduplicated image URL
const getCachedImageUrl = async (originalUrl: string): Promise<string> => {
  // Check cache first
  const cached = imageCache.get(originalUrl);
  if (cached && Date.now() - cached.timestamp < CACHE_DURATION) {
    if (cached.success) {
      return cached.url;
    } else {
      // Use Steam CDN for known failures
      const steamUrl = getSteamCdnUrl(originalUrl);
      return steamUrl || originalUrl;
    }
  }

  // Check if request is already pending
  if (pendingImageRequests.has(originalUrl)) {
    return pendingImageRequests.get(originalUrl)!;
  }

  // Create new request
  const requestPromise = new Promise<string>((resolve) => {
    const img = new Image();
    const timeout = setTimeout(() => {
      // On timeout, try Steam CDN
      const steamUrl = getSteamCdnUrl(originalUrl);
      imageCache.set(originalUrl, { url: steamUrl || originalUrl, timestamp: Date.now(), success: false });
      resolve(steamUrl || originalUrl);
    }, 3000); // Reduced timeout for faster fallback

    img.onload = () => {
      clearTimeout(timeout);
      imageCache.set(originalUrl, { url: originalUrl, timestamp: Date.now(), success: true });
      resolve(originalUrl);
    };

    img.onerror = () => {
      clearTimeout(timeout);
      const steamUrl = getSteamCdnUrl(originalUrl);
      imageCache.set(originalUrl, { url: steamUrl || originalUrl, timestamp: Date.now(), success: false });
      resolve(steamUrl || originalUrl);
    };

    img.src = originalUrl;
  });

  pendingImageRequests.set(originalUrl, requestPromise);

  // Clean up pending request after completion
  requestPromise.finally(() => {
    pendingImageRequests.delete(originalUrl);
  });

  return requestPromise;
};

const ImageWithFallback: React.FC<ImageWithFallbackProps> = ({
  src,
  fallback,
  alt,
  className = '',
  style = {},
  onLoad,
  onError
}) => {
  const [hasError, setHasError] = useState(false);
  const [isLoading, setIsLoading] = useState(true);
  const [currentSrc, setCurrentSrc] = useState(src);
  const [resolvedUrl, setResolvedUrl] = useState<string | null>(null);

  useEffect(() => {
    setHasError(false);
    setIsLoading(true);
    setCurrentSrc(src);
    setResolvedUrl(null);

    // Use the deduplicated image loading system
    getCachedImageUrl(src).then((url) => {
      setResolvedUrl(url);
      setCurrentSrc(url);
    }).catch((error) => {
      console.error(`Failed to resolve image URL for ${alt}:`, error);
      setHasError(true);
      setIsLoading(false);
      onError?.();
    });
  }, [src, alt, onError]);

  const handleLoad = () => {
    setIsLoading(false);
    onLoad?.();
  };

  const handleError = () => {
    setHasError(true);
    setIsLoading(false);
    onError?.();
  };

  if (!src || hasError || !resolvedUrl) {
    return (
      <>
        {fallback || (
          <div
            className={`${className} flex items-center justify-center`}
            style={{
              ...style,
              background: 'linear-gradient(135deg, var(--theme-bg-tertiary), var(--theme-bg-secondary))'
            }}
          >
            {!resolvedUrl && !hasError ? (
              <Loader className="w-6 h-6 animate-spin" style={{ color: 'var(--theme-text-muted)' }} />
            ) : (
              <Gamepad2
                className="w-12 h-12"
                style={{ color: 'var(--theme-text-muted)' }}
              />
            )}
          </div>
        )}
      </>
    );
  }

  return (
    <div className="relative">
      {isLoading && (
        <div
          className={`${className} flex items-center justify-center absolute inset-0`}
          style={{
            ...style,
            backgroundColor: 'var(--theme-bg-tertiary)'
          }}
        >
          <Loader className="w-6 h-6 animate-spin" />
        </div>
      )}
      <img
        key={currentSrc}
        src={currentSrc}
        alt={alt}
        className={className}
        style={{
          ...style,
          opacity: isLoading ? 0 : 1,
          transition: 'opacity 0.3s'
        }}
        onLoad={handleLoad}
        onError={handleError}
        loading="lazy"
        crossOrigin="anonymous"
      />
    </div>
  );
};

export default ImageWithFallback;
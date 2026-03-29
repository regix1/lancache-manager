import { createContext, useContext } from 'react';

/**
 * Context for cache-busting game images after a manual refresh.
 * When the value changes, all GameImage URLs get a new query param,
 * forcing the browser to bypass its HTTP cache.
 */
export const ImageCacheContext = createContext(0);

/**
 * Context providing a callback to invalidate (bust) the image cache.
 * Call this after new game detection results are loaded to force
 * GameImage components to re-fetch images that may now be available.
 */
export const ImageInvalidateContext = createContext<(() => void) | null>(null);

/**
 * Hook to obtain the image cache invalidation function.
 * Returns null when used outside of an ImageInvalidateContext provider.
 */
export const useInvalidateImages = (): (() => void) | null => useContext(ImageInvalidateContext);

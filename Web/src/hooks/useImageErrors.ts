import { useCallback, useContext, useEffect, useState } from 'react';
import { ImageCacheContext } from '@components/common/ImageCacheContext';

/**
 * Remembers which banners answered with an error so a row draws its placeholder instead of a broken
 * image, and forgets them whenever the image cache version moves.
 *
 * The forgetting is the point. A row stops rendering its GameImage the moment one errors, so the
 * component's own reset never gets to run, and without this the slot stays a placeholder for the
 * life of the page even after the fetch pass has stored the artwork. A version bump is the signal
 * that the stored images changed, which is exactly when a previous failure stops being evidence.
 */
export function useImageErrors(): {
  imageErrors: Set<string>;
  handleImageError: (imageId: string) => void;
} {
  const cacheBuster = useContext(ImageCacheContext);
  const [imageErrors, setImageErrors] = useState<Set<string>>(new Set());

  useEffect(() => {
    setImageErrors(new Set());
  }, [cacheBuster]);

  const handleImageError = useCallback((imageId: string): void => {
    setImageErrors((prev) => new Set(prev).add(imageId));
  }, []);

  return { imageErrors, handleImageError };
}

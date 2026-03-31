import { useState, useEffect, useContext } from 'react';
import { ImageCacheContext } from '@components/common/ImageCacheContext';
import ApiService from '@services/api.service';

let availableIds = new Set<string>();
let lastCacheBuster = -1;
let fetchPromise: Promise<void> | null = null;

function fetchAvailableIds(cacheBuster: number): Promise<void> {
  if (lastCacheBuster === cacheBuster && availableIds.size > 0) {
    return Promise.resolve();
  }
  if (fetchPromise && lastCacheBuster === cacheBuster) {
    return fetchPromise;
  }
  lastCacheBuster = cacheBuster;
  fetchPromise = ApiService.getAvailableGameImages()
    .then((ids: string[]) => {
      availableIds = new Set(ids);
    })
    .catch(() => {
      availableIds = new Set();
    })
    .finally(() => {
      fetchPromise = null;
    });
  return fetchPromise;
}

export function useAvailableGameImages(): Set<string> {
  const cacheBuster = useContext(ImageCacheContext);
  const [ids, setIds] = useState<Set<string>>(availableIds);

  useEffect(() => {
    fetchAvailableIds(cacheBuster).then(() => {
      setIds(new Set(availableIds));
    });
  }, [cacheBuster]);

  return ids;
}

import { useCallback, useState } from 'react';
import { storage } from '@utils/storage';

interface ColorHistoryActions {
  commitColor: (key: string, previousColor: string) => void;
  restoreColor: (key: string, applyColor: (color: string) => void) => void;
  hasHistory: (key: string) => boolean;
}

export function useColorHistory(keyPrefix: string): ColorHistoryActions {
  // Counter to force re-render when history changes
  const [, setHistoryVersion] = useState(0);

  const parseHistory = (raw: string | null): string[] => {
    if (!raw) return [];
    try {
      const parsed = JSON.parse(raw);
      return Array.isArray(parsed) ? parsed : [raw];
    } catch {
      return [raw];
    }
  };

  const commitColor = useCallback(
    (key: string, previousColor: string) => {
      const historyKey = `${keyPrefix}_${key}`;
      const originalKey = `${keyPrefix}_${key}_original`;
      const history = parseHistory(storage.getItem(historyKey));

      if (!storage.getItem(originalKey)) {
        storage.setItem(originalKey, previousColor);
      }

      history.unshift(previousColor);
      if (history.length > 3) history.pop();
      storage.setItem(historyKey, JSON.stringify(history));
      setHistoryVersion((v) => v + 1);
    },
    [keyPrefix]
  );

  const restoreColor = useCallback(
    (key: string, applyColor: (color: string) => void) => {
      const historyKey = `${keyPrefix}_${key}`;
      const originalKey = `${keyPrefix}_${key}_original`;
      const history = parseHistory(storage.getItem(historyKey));

      if (history.length > 0) {
        const previousColor = history.shift()!;
        if (history.length > 0) {
          storage.setItem(historyKey, JSON.stringify(history));
        } else {
          storage.removeItem(historyKey);
        }
        applyColor(previousColor);
      } else {
        const originalColor = storage.getItem(originalKey);
        if (originalColor) {
          applyColor(originalColor);
          storage.removeItem(originalKey);
        }
      }
      setHistoryVersion((v) => v + 1);
    },
    [keyPrefix]
  );

  const hasHistory = useCallback(
    (key: string): boolean => {
      const historyKey = `${keyPrefix}_${key}`;
      const originalKey = `${keyPrefix}_${key}_original`;
      return !!(storage.getItem(historyKey) || storage.getItem(originalKey));
    },
    [keyPrefix]
  );

  return { commitColor, restoreColor, hasHistory };
}

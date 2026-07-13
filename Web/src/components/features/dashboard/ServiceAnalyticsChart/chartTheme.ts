import { useEffect, useState } from 'react';
import { APP_EVENTS } from '@utils/constants';

export function getThemeColor(name: string): string {
  return getComputedStyle(document.documentElement).getPropertyValue(name).trim();
}

export function useThemeRevision(): number {
  const [revision, setRevision] = useState(0);

  useEffect(() => {
    const updateRevision = () => setRevision((current) => current + 1);
    window.addEventListener(APP_EVENTS.THEME_CHANGE, updateRevision);
    return () => window.removeEventListener(APP_EVENTS.THEME_CHANGE, updateRevision);
  }, []);

  return revision;
}

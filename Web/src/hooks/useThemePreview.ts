import { useEffect, useState } from 'react';
import themeService from '@services/theme.service';
import { APP_EVENTS } from '@utils/constants';

export const useThemePreview = (): string | null => {
  const [previewTheme, setPreviewTheme] = useState<string | null>(() =>
    themeService.getPreviewTheme()
  );

  useEffect(() => {
    const readPreviewTheme = () => setPreviewTheme(themeService.getPreviewTheme());
    window.addEventListener(APP_EVENTS.THEME_PREVIEW_CHANGE, readPreviewTheme);
    return () => window.removeEventListener(APP_EVENTS.THEME_PREVIEW_CHANGE, readPreviewTheme);
  }, []);

  return previewTheme;
};

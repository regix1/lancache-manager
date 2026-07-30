import { useEffect, useState } from 'react';
import { Eye } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import themeService from '@services/theme.service';
import { Button } from '@components/ui/Button';
import { Tooltip } from '@components/ui/Tooltip';
import { APP_EVENTS } from '@utils/constants';

interface ThemePreviewBannerProps {
  iconOnly?: boolean;
}

/**
 * Header control shown on every page while a theme preview is active, so the
 * preview can be exited from anywhere instead of only Management > Theme.
 */
export default function ThemePreviewBanner({ iconOnly = false }: ThemePreviewBannerProps) {
  const { t } = useTranslation();
  const [previewId, setPreviewId] = useState<string | null>(() => themeService.getPreviewTheme());
  const [previewName, setPreviewName] = useState<string>('');
  const [exiting, setExiting] = useState(false);

  // A preview can start or end from anywhere - Management > Theme, this button, or committing a
  // theme while a preview is running - so the stored id is re-read on every change, not just at mount
  useEffect(() => {
    const readPreviewId = () => setPreviewId(themeService.getPreviewTheme());
    window.addEventListener(APP_EVENTS.THEME_PREVIEW_CHANGE, readPreviewId);
    return () => window.removeEventListener(APP_EVENTS.THEME_PREVIEW_CHANGE, readPreviewId);
  }, []);

  useEffect(() => {
    if (!previewId) {
      setPreviewName('');
      return;
    }
    let mounted = true;
    themeService.getTheme(previewId).then((theme) => {
      if (mounted) setPreviewName(theme?.meta.name || previewId);
    });
    return () => {
      mounted = false;
    };
  }, [previewId]);

  if (!previewId) return null;

  // Mirrors ThemeManager's preview-off branch so exiting behaves identically
  const exitPreview = async () => {
    setExiting(true);
    try {
      const originalTheme = themeService.getOriginalThemeBeforePreview() || 'dark-default';
      await themeService.setTheme(originalTheme);
      themeService.clearPreviewTheme();
      themeService.clearOriginalThemeBeforePreview();
    } finally {
      // The clear above hides this button, but if restoring the theme threw it stays on screen and
      // has to be clickable again
      setExiting(false);
    }
  };

  const label = t('management.themes.actions.stopPreview');

  return (
    <Tooltip content={t('management.themes.previewingBanner', { name: previewName || previewId })}>
      <Button
        variant="filled"
        color="blue"
        size={iconOnly ? 'sm' : 'md'}
        onClick={exitPreview}
        disabled={exiting}
        aria-label={label}
        leftSection={<Eye className="w-4 h-4" />}
        className={iconOnly ? undefined : 'min-h-10'}
      >
        {!iconOnly && label}
      </Button>
    </Tooltip>
  );
}

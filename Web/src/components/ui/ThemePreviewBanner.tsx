import { useEffect, useState } from 'react';
import { Eye } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import themeService from '@services/theme.service';
import { Button } from '@components/ui/Button';
import { Tooltip } from '@components/ui/Tooltip';

interface ThemePreviewBannerProps {
  iconOnly?: boolean;
}

/**
 * Header control shown on every page while a theme preview is active, so the
 * preview can be exited from anywhere instead of only Management > Theme.
 */
export default function ThemePreviewBanner({ iconOnly = false }: ThemePreviewBannerProps) {
  const { t } = useTranslation();
  const [previewId] = useState<string | null>(() => themeService.getPreviewTheme());
  const [previewName, setPreviewName] = useState<string>('');
  const [exiting, setExiting] = useState(false);

  useEffect(() => {
    if (!previewId) return;
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
    const originalTheme = themeService.getOriginalThemeBeforePreview() || 'dark-default';
    await themeService.setTheme(originalTheme);
    themeService.clearPreviewTheme();
    themeService.clearOriginalThemeBeforePreview();
    window.location.reload();
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

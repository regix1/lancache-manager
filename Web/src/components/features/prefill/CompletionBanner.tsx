import { useTranslation } from 'react-i18next';
import { Card } from '../../ui/Card';
import { Button } from '../../ui/Button';
import { CheckCircle2, X } from 'lucide-react';
import type { BackgroundCompletion } from './hooks/prefillTypes';

interface CompletionBannerProps {
  completion: BackgroundCompletion;
  onDismiss: () => void;
}

export function CompletionBanner({ completion, onDismiss }: CompletionBannerProps) {
  const { t } = useTranslation();

  return (
    <Card
      padding="md"
      className="overflow-hidden border-[color-mix(in_srgb,var(--theme-success)_50%,transparent)] animate-fade-in"
    >
      <div className="flex items-center justify-between">
        <div className="flex items-center gap-3">
          <div className="w-10 h-10 rounded-lg flex items-center justify-center bg-[color-mix(in_srgb,var(--theme-success)_15%,transparent)]">
            <CheckCircle2 className="h-5 w-5 text-[var(--theme-success)]" />
          </div>
          <div>
            <p className="font-medium text-themed-primary">{t('prefill.completion.title')}</p>
            <p className="text-sm text-themed-muted">{completion.message}</p>
          </div>
        </div>
        <Button variant="outline" size="sm" onClick={onDismiss} className="flex-shrink-0">
          <X className="h-4 w-4" />
          {t('common.dismiss')}
        </Button>
      </div>
    </Card>
  );
}

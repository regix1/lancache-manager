import { useTranslation } from 'react-i18next';
import { Card } from '../../ui/Card';
import { Button } from '../../ui/Button';
import { CheckCircle2 } from 'lucide-react';
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
      className="overflow-hidden border-[var(--theme-success-strong)] animate-fade-in"
    >
      <div className="flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
        <div className="flex items-center gap-3">
          <div className="w-10 h-10 rounded-lg flex items-center justify-center bg-[var(--theme-success-subtle)]">
            <CheckCircle2 className="h-5 w-5 text-[var(--theme-success)]" />
          </div>
          <div>
            <p className="font-medium text-themed-primary">{t('prefill.completion.title')}</p>
            <p className="text-sm text-themed-muted">{completion.message}</p>
          </div>
        </div>
        <Button
          variant="filled"
          color="gray"
          size="sm"
          onClick={onDismiss}
          className="flex-shrink-0 min-h-[44px] sm:min-h-8 w-full sm:w-auto"
        >
          {t('common.dismiss')}
        </Button>
      </div>
    </Card>
  );
}

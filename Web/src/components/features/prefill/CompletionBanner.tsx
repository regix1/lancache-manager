import { Card } from '../../ui/Card';
import { Button } from '../../ui/Button';
import { CheckCircle2, X } from 'lucide-react';

interface BackgroundCompletion {
  completedAt: string;
  message: string;
  duration?: number;
}

interface CompletionBannerProps {
  completion: BackgroundCompletion;
  onDismiss: () => void;
}

export function CompletionBanner({ completion, onDismiss }: CompletionBannerProps) {
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
            <p className="font-medium text-themed-primary">Download Completed</p>
            <p className="text-sm text-themed-muted">{completion.message}</p>
          </div>
        </div>
        <Button variant="outline" size="sm" onClick={onDismiss} className="flex-shrink-0">
          <X className="h-4 w-4" />
          Dismiss
        </Button>
      </div>
    </Card>
  );
}

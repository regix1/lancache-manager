import { Card, CardContent } from '../../ui/Card';
import { Loader2 } from 'lucide-react';

interface PrefillLoadingStateProps {
  status: 'checking' | 'creating';
}

export function PrefillLoadingState({ status }: PrefillLoadingStateProps) {
  const title =
    status === 'creating'
      ? 'Creating a new prefill session...'
      : 'Looking for an active prefill session...';

  return (
    <div className="animate-fade-in">
      <Card className="max-w-2xl mx-auto">
        <CardContent className="py-12">
          <div className="flex flex-col items-center justify-center gap-4">
            <div className="w-16 h-16 rounded-xl flex items-center justify-center bg-[color-mix(in_srgb,var(--theme-steam)_15%,transparent)]">
              <Loader2 className="h-8 w-8 animate-spin text-[var(--theme-steam)]" />
            </div>
            <div className="text-center">
              <p className="text-lg font-medium text-themed-primary">
                {title}
              </p>
              <p className="text-sm text-themed-muted mt-1">This may take a moment</p>
            </div>
          </div>
        </CardContent>
      </Card>
    </div>
  );
}

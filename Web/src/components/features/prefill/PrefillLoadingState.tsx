import { useTranslation } from 'react-i18next';
import { Card, CardContent } from '../../ui/Card';
import { Loader2 } from 'lucide-react';

interface PrefillLoadingStateProps {
  status: 'checking' | 'creating';
  serviceId: string;
}

export function PrefillLoadingState({ status, serviceId }: PrefillLoadingStateProps) {
  const { t } = useTranslation();
  const isEpic = serviceId === 'epic';

  const title =
    status === 'creating'
      ? t('prefill.loading.creatingSession')
      : t('prefill.loading.lookingForSession');

  return (
    <div className="animate-fade-in">
      <Card className="max-w-2xl mx-auto">
        <CardContent className="py-12">
          <div className="flex flex-col items-center justify-center gap-4">
            <div
              className={`w-16 h-16 rounded-xl flex items-center justify-center ${
                isEpic ? 'prefill-loading-icon--epic' : 'prefill-loading-icon--steam'
              }`}
            >
              <Loader2
                className={`h-8 w-8 animate-spin ${
                  isEpic ? 'prefill-loading-spinner--epic' : 'prefill-loading-spinner--steam'
                }`}
              />
            </div>
            <div className="text-center">
              <p className="text-lg font-medium text-themed-primary">{title}</p>
              <p className="text-sm text-themed-muted mt-1">{t('prefill.loading.mayTakeMoment')}</p>
            </div>
          </div>
        </CardContent>
      </Card>
    </div>
  );
}

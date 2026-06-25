import { useTranslation } from 'react-i18next';
import { Card, CardContent } from '../../ui/Card';
import LoadingSpinner from '@components/common/LoadingSpinner';

interface PrefillLoadingStateProps {
  status: 'checking' | 'creating';
  serviceId: string;
}

// Full literal class names so Tailwind's content scanner keeps these @layer components
// rules in the production build. Building them dynamically (e.g. `prefill-loading-spinner--${x}`)
// hides the class strings from the scanner, which then purges the color/background rules and
// the spinner renders with no accent color (white).
const LOADING_ICON_CLASS: Record<'steam' | 'epic' | 'battlenet' | 'riot' | 'xbox', string> = {
  steam: 'prefill-loading-icon--steam',
  epic: 'prefill-loading-icon--epic',
  battlenet: 'prefill-loading-icon--battlenet',
  riot: 'prefill-loading-icon--riot',
  xbox: 'prefill-loading-icon--xbox'
};

const LOADING_SPINNER_CLASS: Record<'steam' | 'epic' | 'battlenet' | 'riot' | 'xbox', string> = {
  steam: 'prefill-loading-spinner--steam',
  epic: 'prefill-loading-spinner--epic',
  battlenet: 'prefill-loading-spinner--battlenet',
  riot: 'prefill-loading-spinner--riot',
  xbox: 'prefill-loading-spinner--xbox'
};

export function PrefillLoadingState({ status, serviceId }: PrefillLoadingStateProps) {
  const { t } = useTranslation();
  const accent: 'steam' | 'epic' | 'battlenet' | 'riot' | 'xbox' =
    serviceId === 'epic'
      ? 'epic'
      : serviceId === 'battlenet'
        ? 'battlenet'
        : serviceId === 'riot'
          ? 'riot'
          : serviceId === 'xbox'
            ? 'xbox'
            : 'steam';

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
              className={`w-16 h-16 rounded-xl flex items-center justify-center ${LOADING_ICON_CLASS[accent]}`}
            >
              <LoadingSpinner inline size="xl" className={LOADING_SPINNER_CLASS[accent]} />
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

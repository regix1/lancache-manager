import { useTranslation } from 'react-i18next';
import { Card, CardContent } from '../../ui/Card';
import { Button } from '../../ui/Button';
import { SteamIcon } from '@components/ui/SteamIcon';
import { Loader2, Play, AlertCircle, Shield } from 'lucide-react';

interface PrefillStartScreenProps {
  error: string | null;
  isConnecting: boolean;
  onCreateSession: () => void;
}

export function PrefillStartScreen({ error, isConnecting, onCreateSession }: PrefillStartScreenProps) {
  const { t } = useTranslation();

  return (
    <div className="animate-fade-in">
      <Card className="max-w-2xl mx-auto">
        <CardContent className="py-12">
          <div className="flex flex-col items-center text-center space-y-6">
            {/* Steam Icon */}
            <div className="w-20 h-20 rounded-2xl flex items-center justify-center bg-[var(--theme-steam)]">
              <SteamIcon size={40} className="text-white" />
            </div>

            <div className="space-y-2">
              <h2 className="text-2xl font-bold text-themed-primary">{t('prefill.title')}</h2>
              <p className="text-themed-muted max-w-md">
                {t('prefill.startScreen.description')}
              </p>
            </div>

            {error && (
              <div className="w-full max-w-md p-4 rounded-lg flex items-center gap-3 bg-[var(--theme-error-bg)] border border-[color-mix(in_srgb,var(--theme-error)_30%,transparent)]">
                <AlertCircle className="h-5 w-5 flex-shrink-0 text-[var(--theme-error)]" />
                <span className="text-sm text-[var(--theme-error-text)]">{error}</span>
              </div>
            )}

            <div className="flex flex-col items-center gap-3 pt-2">
              <Button
                onClick={onCreateSession}
                disabled={isConnecting}
                variant="filled"
                size="lg"
                className="min-w-[200px]"
              >
                {isConnecting ? (
                  <>
                    <Loader2 className="h-5 w-5 animate-spin" />
                    {t('prefill.startScreen.connecting')}
                  </>
                ) : (
                  <>
                    <Play className="h-5 w-5" />
                    {t('prefill.startScreen.startSession')}
                  </>
                )}
              </Button>
              <p className="text-xs text-themed-muted flex items-center gap-1.5">
                <Shield className="h-3.5 w-3.5" />
                {t('prefill.startScreen.requiresLogin')}
              </p>
            </div>
          </div>
        </CardContent>
      </Card>
    </div>
  );
}

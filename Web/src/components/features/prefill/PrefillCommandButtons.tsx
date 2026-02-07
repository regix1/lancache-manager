import { useTranslation } from 'react-i18next';
import { Card } from '../../ui/Card';
import { Shield } from 'lucide-react';
import type { CommandType } from './types';

interface PrefillCommandButtonsProps {
  isLoggedIn: boolean;
  isExecuting: boolean;
  isPrefillActive: boolean;
  isSessionActive: boolean;
  isUserAuthenticated: boolean;
  selectedAppIds: number[];
  selectedOS: string[];
  maxConcurrency: string;
  onCommandClick: (commandType: CommandType) => void;
  onSelectedOSChange: (values: string[]) => void;
  onMaxConcurrencyChange: (value: string) => void;
}

export function PrefillCommandButtons({
  isLoggedIn,
  isExecuting: _isExecuting,
  isPrefillActive: _isPrefillActive,
  isSessionActive: _isSessionActive,
  isUserAuthenticated: _isUserAuthenticated,
  selectedAppIds: _selectedAppIds,
  selectedOS: _selectedOS,
  maxConcurrency: _maxConcurrency,
  onCommandClick: _onCommandClick,
  onSelectedOSChange: _onSelectedOSChange,
  onMaxConcurrencyChange: _onMaxConcurrencyChange
}: PrefillCommandButtonsProps) {
  const { t } = useTranslation();

  return (
    <Card padding="md">
      <div className="space-y-6">
        {/* Login Required Notice */}
        {!isLoggedIn && (
          <div className="p-4 rounded-lg flex items-start gap-3 bg-[color-mix(in_srgb,var(--theme-warning)_10%,transparent)] border border-[color-mix(in_srgb,var(--theme-warning)_25%,transparent)]">
            <Shield className="h-5 w-5 flex-shrink-0 mt-0.5 text-[var(--theme-warning)]" />
            <div>
              <p className="font-medium text-sm text-[var(--theme-warning-text)]">
                {t('prefill.loginNotice.title')}
              </p>
              <p className="text-sm text-themed-muted mt-1">
                {t('prefill.loginNotice.message')}
              </p>
            </div>
          </div>
        )}
      </div>
    </Card>
  );
}

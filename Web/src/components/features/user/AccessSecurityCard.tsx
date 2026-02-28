import React from 'react';
import { useTranslation } from 'react-i18next';
import { Shield, Loader2 } from 'lucide-react';
import { Card } from '@components/ui/Card';
import { EnhancedDropdown } from '@components/ui/EnhancedDropdown';

interface AccessSecurityCardProps {
  guestDurationHours: number;
  onDurationChange: (duration: number) => void;
  updatingDuration: boolean;
  durationOptions: Array<{ value: string; label: string }>;
}

const AccessSecurityCard: React.FC<AccessSecurityCardProps> = ({
  guestDurationHours,
  onDurationChange,
  updatingDuration,
  durationOptions
}) => {
  const { t } = useTranslation();

  const handleDurationChange = (value: string) => {
    onDurationChange(Number(value));
  };

  return (
    <Card padding="none">
      {/* Header */}
      <div className="p-4 sm:p-5 border-b border-themed-secondary">
        <h3 className="text-lg font-semibold flex items-center gap-2 text-themed-primary">
          <Shield className="w-5 h-5 text-themed-accent" />
          {t('user.guest.sections.accessSecurity')}
        </h3>
        <p className="text-sm mt-1 text-themed-muted">
          {t('user.guest.sections.accessSecuritySubtitle')}
        </p>
      </div>

      <div className="p-4 sm:p-5">
        <div className="settings-group settings-group--access">
          <div className="flex flex-col sm:flex-row sm:items-center gap-2 sm:gap-4">
            <div className="toggle-row-label whitespace-nowrap">
              {t('user.guest.sections.sessionDuration')}
            </div>
            <div className="relative">
              <EnhancedDropdown
                options={durationOptions}
                value={guestDurationHours.toString()}
                onChange={handleDurationChange}
                disabled={updatingDuration}
                className="w-48"
              />
              {updatingDuration && (
                <Loader2 className="w-4 h-4 animate-spin absolute right-10 top-1/2 -translate-y-1/2 text-themed-accent" />
              )}
            </div>
          </div>
        </div>
      </div>
    </Card>
  );
};

export default AccessSecurityCard;

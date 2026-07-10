import React from 'react';
import { useTranslation } from 'react-i18next';
import { CheckCircle, User } from 'lucide-react';
import { Card } from '@components/ui/Card';
import { SteamIcon } from '@components/ui/SteamIcon';
import { useSteamAuth } from '@contexts/useSteamAuth';
import { type AuthMode } from '@services/auth.service';
import SteamLoginManager from './SteamLoginManager';
import SteamWebApiStatus from './SteamWebApiStatus';

interface SteamIntegrationCardProps {
  authMode: AuthMode;
  mockMode: boolean;
  onError: (message: string) => void;
  onSuccess: (message: string) => void;
}

const SteamIntegrationCard: React.FC<SteamIntegrationCardProps> = ({
  authMode,
  mockMode,
  onError,
  onSuccess
}) => {
  const { t } = useTranslation();
  const { steamAuthMode } = useSteamAuth();

  return (
    <Card>
      {/* Header: Steam icon + Title + connection badge (mirrors the Epic/Xbox card header) */}
      <div className="flex items-center gap-3 mb-4">
        <div className="w-10 h-10 rounded-lg flex items-center justify-center bg-[var(--theme-steam-subtle)] text-[var(--theme-steam)]">
          <SteamIcon size={20} />
        </div>
        <h3 className="text-lg font-semibold text-themed-primary">
          {t('management.sections.integrations.steamCard.title')}
        </h3>
        <div className="ml-auto flex-shrink-0">
          {steamAuthMode === 'authenticated' ? (
            <span className="inline-flex items-center gap-1.5 px-2.5 py-1 rounded-md text-xs font-medium bg-themed-success text-themed-success">
              <CheckCircle size={14} />
              {t('management.steamAuth.connected')}
            </span>
          ) : (
            <span className="inline-flex items-center gap-1.5 px-2.5 py-1 rounded-md text-xs font-medium bg-themed-secondary text-themed-muted">
              <User size={14} />
              {t('management.steamAuth.anonymous')}
            </span>
          )}
        </div>
      </div>

      <SteamLoginManager
        authMode={authMode}
        mockMode={mockMode}
        onError={onError}
        onSuccess={onSuccess}
      />

      <div className="integration-subsection">
        <SteamWebApiStatus />
      </div>
    </Card>
  );
};

export default SteamIntegrationCard;

import React, { useCallback, useEffect, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Shield } from 'lucide-react';
import { Card } from '@components/ui/Card';
import { Button } from '@components/ui/Button';
import { EnhancedDropdown } from '@components/ui/EnhancedDropdown';
import LoadingSpinner from '@components/common/LoadingSpinner';
import ApiService from '@services/api.service';
import { useAuth } from '@contexts/useAuth';
import { useNotifications } from '@contexts/notifications';
import { useSignalR } from '@contexts/SignalRContext/useSignalR';
import type { GuestDurationResponse } from './AccessSecurityCard.types';
import './AccessSecurityCard.css';

interface AccessSecurityCardProps {
  // Legacy props kept for backward compatibility with GuestConfiguration's call site.
  // The card now sources its own state from /api/auth/guest/config/duration so the
  // `source`/`envVarValue` fields are available for the source label + reset button.
  guestDurationHours: number;
  onDurationChange: (duration: number) => void;
  updatingDuration: boolean;
  durationOptions: { value: string; label: string }[];
}

const AccessSecurityCard: React.FC<AccessSecurityCardProps> = ({ durationOptions }) => {
  const { t } = useTranslation();
  const { isAdmin } = useAuth();
  const { addNotification } = useNotifications();
  const { on, off, connectionState } = useSignalR();

  const [state, setState] = useState<GuestDurationResponse | null>(null);
  const [isSaving, setIsSaving] = useState(false);

  const fetchGuestDuration = useCallback(async (signal?: AbortSignal) => {
    try {
      const data = await ApiService.getGuestSessionDuration(signal);
      setState(data);
    } catch (error: unknown) {
      if (error instanceof Error && error.name === 'AbortError') return;
      console.error('Failed to load guest session duration:', error);
    }
  }, []);

  // Initial load
  useEffect(() => {
    const controller = new AbortController();
    void fetchGuestDuration(controller.signal);
    return () => controller.abort();
  }, [fetchGuestDuration]);

  // Live updates via SignalR (broadcasts effective duration from controller after every change)
  useEffect(() => {
    const handleDurationUpdated = (data: { durationHours: number }) => {
      setState((prev) => (prev ? { ...prev, durationHours: data.durationHours } : prev));
      // Refetch full payload to refresh source/envVarValue (the broadcast only contains
      // durationHours, but a clear-override flips `source` from 'ui' to 'config').
      void fetchGuestDuration();
    };
    on('GuestDurationUpdated', handleDurationUpdated);
    return () => off('GuestDurationUpdated', handleDurationUpdated);
  }, [on, off, fetchGuestDuration]);

  // Recover after SignalR reconnect
  useEffect(() => {
    if (connectionState === 'connected') {
      void fetchGuestDuration();
    }
  }, [connectionState, fetchGuestDuration]);

  const persistDuration = async (next: number | null, previous: GuestDurationResponse) => {
    setIsSaving(true);
    // Optimistic update — only the durationHours can be guessed; source/envVarValue
    // resolve from server response.
    if (next !== null) {
      setState((prev) => (prev ? { ...prev, durationHours: next } : prev));
    }
    try {
      const data = await ApiService.setGuestSessionDuration(next);
      setState(data);
    } catch (error: unknown) {
      // Revert optimistic update
      setState(previous);
      const message = error instanceof Error ? error.message : 'network';
      addNotification({
        type: 'generic',
        status: 'failed',
        message: t('user.guest.guestDurationToggle.error', { status: message }),
        details: { notificationType: 'error' }
      });
    } finally {
      setIsSaving(false);
    }
  };

  const handleDurationChange = (value: string) => {
    if (!state || isSaving || !isAdmin) return;
    const parsed = Number(value);
    if (!Number.isFinite(parsed)) return;
    void persistDuration(parsed, state);
  };

  const handleResetToDefault = () => {
    if (!state || isSaving || !isAdmin || state.source !== 'ui') return;
    void persistDuration(null, state);
  };

  const getSourceLabel = (current: GuestDurationResponse): string => {
    if (current.source === 'ui') {
      return t('user.guest.guestDurationToggle.source.ui');
    }
    if (current.source === 'config') {
      if (current.envVarValue !== current.durationHours) {
        return t('user.guest.guestDurationToggle.source.config');
      }
      return t('user.guest.guestDurationToggle.source.default');
    }
    return t('user.guest.guestDurationToggle.source.default');
  };

  const dropdownDisabled = !isAdmin || isSaving || state === null;
  const dropdownTitle = !isAdmin ? t('user.guest.guestDurationToggle.adminRequired') : undefined;

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

            {state === null ? (
              <LoadingSpinner inline size="sm" />
            ) : (
              <div className="access-security-card__toggle-row">
                <div className="access-security-card__dropdown-wrapper" title={dropdownTitle}>
                  <EnhancedDropdown
                    options={durationOptions}
                    value={state.durationHours.toString()}
                    onChange={handleDurationChange}
                    disabled={dropdownDisabled}
                    className="w-48"
                  />
                  {isSaving && (
                    <LoadingSpinner
                      inline
                      size="sm"
                      className="absolute right-10 top-1/2 -translate-y-1/2 text-themed-accent"
                    />
                  )}
                </div>
                <span className="access-security-card__source-label">{getSourceLabel(state)}</span>
                {isAdmin && (
                  <Button
                    variant="subtle"
                    size="xs"
                    disabled={state.source !== 'ui' || isSaving}
                    onClick={handleResetToDefault}
                    className="access-security-card__reset-button"
                  >
                    {t('user.guest.guestDurationToggle.resetToDefault')}
                  </Button>
                )}
              </div>
            )}
          </div>
        </div>
      </div>
    </Card>
  );
};

export default AccessSecurityCard;

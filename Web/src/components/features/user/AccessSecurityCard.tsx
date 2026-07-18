import React, { useCallback, useEffect, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Shield } from 'lucide-react';
import { Card } from '@components/ui/Card';
import { Button } from '@components/ui/Button';
import { EnhancedDropdown } from '@components/ui/EnhancedDropdown';
import { Tooltip } from '@components/ui/Tooltip';
import LoadingSpinner from '@components/common/LoadingSpinner';
import ApiService from '@services/api.service';
import { useAuth } from '@contexts/useAuth';
import { useErrorHandler } from '@hooks/useErrorHandler';
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
  const { notifyError } = useErrorHandler();
  const { on, off, connectionState } = useSignalR();

  const [state, setState] = useState<GuestDurationResponse | null>(null);
  const [isSaving, setIsSaving] = useState(false);

  const fetchGuestDuration = useCallback(
    async (signal?: AbortSignal) => {
      try {
        const data = await ApiService.getGuestSessionDuration(signal);
        setState(data);
      } catch (error: unknown) {
        notifyError(t('user.guest.errors.loadSessionDuration'), error, {
          logLabel: 'Failed to load guest session duration'
        });
      }
    },
    [notifyError, t]
  );

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
      notifyError(t('user.guest.guestDurationToggle.error'), error, {
        logLabel: 'Failed to update guest session duration'
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
      </div>

      <div className="p-4 sm:p-5">
        <div className="mgmt-list user-settings-list">
          <div className="mgmt-row">
            <div className="mgmt-row__body">
              <p className="mgmt-row__title">{t('user.guest.sections.sessionDuration')}</p>
              {state && <p className="mgmt-row__meta">{getSourceLabel(state)}</p>}
            </div>

            <div className="mgmt-row__actions">
              {state === null ? (
                <LoadingSpinner inline size="sm" />
              ) : (
                <>
                  {(() => {
                    const durationControl = (
                      <span className="user-settings-dropdown">
                        <EnhancedDropdown
                          options={durationOptions}
                          value={state.durationHours.toString()}
                          onChange={handleDurationChange}
                          disabled={dropdownDisabled}
                          size="md"
                          className="w-40"
                        />
                        {isSaving && (
                          <LoadingSpinner
                            inline
                            size="sm"
                            className="user-settings-inline-spinner"
                          />
                        )}
                      </span>
                    );
                    // Wrap in a Tooltip only when there is an explanatory title
                    // (non-admins); an empty title would render a blank hover box.
                    return dropdownTitle ? (
                      <Tooltip content={dropdownTitle} position="top">
                        {durationControl}
                      </Tooltip>
                    ) : (
                      durationControl
                    );
                  })()}
                  {isAdmin && (
                    <Button
                      variant="filled"
                      color="gray"
                      size="md"
                      disabled={state.source !== 'ui' || isSaving}
                      onClick={handleResetToDefault}
                    >
                      {t('user.guest.guestDurationToggle.resetToDefault')}
                    </Button>
                  )}
                </>
              )}
            </div>
          </div>
        </div>
      </div>
    </Card>
  );
};

export default AccessSecurityCard;

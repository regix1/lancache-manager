import React, { useCallback, useEffect, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Shield } from 'lucide-react';
import { AccordionSection } from '@components/ui/AccordionSection';
import { useAccordionGroupItem } from '@contexts/AccordionGroupContext';
import { Button } from '@components/ui/Button';
import { EnhancedDropdown } from '@components/ui/EnhancedDropdown';
import { Tooltip } from '@components/ui/Tooltip';
import Badge from '@components/ui/Badge';
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

  const [expanded, setExpanded] = useState(false);
  useAccordionGroupItem('guest-access-security', expanded, () => setExpanded((prev) => !prev));
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

  useEffect(() => {
    const controller = new AbortController();
    void fetchGuestDuration(controller.signal);
    return () => controller.abort();
  }, [fetchGuestDuration]);

  useEffect(() => {
    const handleDurationUpdated = (data: { durationHours: number }) => {
      setState((prev) => (prev ? { ...prev, durationHours: data.durationHours } : prev));
      void fetchGuestDuration();
    };
    on('GuestDurationUpdated', handleDurationUpdated);
    return () => off('GuestDurationUpdated', handleDurationUpdated);
  }, [on, off, fetchGuestDuration]);

  useEffect(() => {
    if (connectionState === 'connected') {
      void fetchGuestDuration();
    }
  }, [connectionState, fetchGuestDuration]);

  const persistDuration = async (next: number | null, previous: GuestDurationResponse) => {
    setIsSaving(true);
    if (next !== null) {
      setState((prev) => (prev ? { ...prev, durationHours: next } : prev));
    }
    try {
      const data = await ApiService.setGuestSessionDuration(next);
      setState(data);
    } catch (error: unknown) {
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

  const durationBadgeLabel =
    state === null
      ? null
      : (durationOptions.find((option) => option.value === state.durationHours.toString())?.label ??
        t(`user.guest.durationOptions.${state.durationHours}`));

  return (
    <AccordionSection
      title={t('user.guest.sections.accessSecurity')}
      description={t('user.guest.sections.accessSecuritySubtitle')}
      icon={Shield}
      iconColor="var(--theme-icon-green)"
      isExpanded={expanded}
      onToggle={() => setExpanded((prev) => !prev)}
      badge={durationBadgeLabel ? <Badge variant="neutral">{durationBadgeLabel}</Badge> : undefined}
    >
      <div className="mgmt-list divided-list user-settings-list">
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
                        className="w-40 control-h-md"
                      />
                      {isSaving && (
                        <LoadingSpinner inline size="sm" className="user-settings-inline-spinner" />
                      )}
                    </span>
                  );
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
                    className="control-h-md"
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
    </AccordionSection>
  );
};

export default AccessSecurityCard;

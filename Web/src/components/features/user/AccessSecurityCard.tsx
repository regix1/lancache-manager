import React, { useCallback, useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Shield } from 'lucide-react';
import { AccordionSection } from '@components/ui/AccordionSection';
import { HelpPopover, HelpSection } from '@components/ui/HelpPopover';
import { useAccordionGroupItem } from '@contexts/AccordionGroupContext';
import { Button } from '@components/ui/Button';
import { EnhancedDropdown } from '@components/ui/EnhancedDropdown';
import { Tooltip } from '@components/ui/Tooltip';
import {
  SectionErrorChip,
  SectionHeaderActions,
  SectionHeaderChip
} from '@components/ui/SectionHeaderActions';
import { ErrorBlock } from '@components/ui/ErrorBlock';
import LoadingSpinner from '@components/common/LoadingSpinner';
import ApiService from '@services/api.service';
import { getErrorMessage } from '@utils/error';
import { useAuth } from '@contexts/useAuth';
import { useErrorHandler } from '@hooks/useErrorHandler';
import { useSignalR } from '@contexts/SignalRContext/useSignalR';
import { useReconnectRefetch } from '@hooks/useReconnectRefetch';
import type { GuestDurationResponse } from './AccessSecurityCard.types';

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
  const { on, off, isConnected } = useSignalR();

  const [expanded, setExpanded] = useState(false);
  useAccordionGroupItem('guest-access-security', expanded, () => setExpanded((prev) => !prev));
  const [state, setState] = useState<GuestDurationResponse | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [isSaving, setIsSaving] = useState(false);
  const durationRequestRef = useRef(0);

  const fetchGuestDuration = useCallback(async (signal?: AbortSignal) => {
    // Mount, the duration event, reconnect and Retry can overlap; only the newest request writes
    // the value or the error.
    const request = ++durationRequestRef.current;
    try {
      const data = await ApiService.getGuestSessionDuration(signal);
      if (request !== durationRequestRef.current) return;
      setState(data);
      setLoadError(null);
    } catch (error: unknown) {
      if (request !== durationRequestRef.current) return;
      setLoadError(getErrorMessage(error));
    }
  }, []);

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

  useReconnectRefetch(isConnected, fetchGuestDuration);

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
    if (current.envVarValue !== current.durationHours) {
      return t('user.guest.guestDurationToggle.source.config');
    }
    return t('user.guest.guestDurationToggle.source.default');
  };

  const showErrorChip = loadError !== null && !expanded;
  const dropdownDisabled = !isAdmin || isSaving || state === null;
  const dropdownTitle = !isAdmin ? t('user.guest.guestDurationToggle.adminRequired') : undefined;

  const durationBadgeLabel =
    state === null
      ? null
      : (durationOptions.find((option) => option.value === state.durationHours.toString())?.label ??
        t(`user.guest.durationOptions.${state.durationHours}`));

  const helpAccessory = (
    <HelpPopover position="left" width={320}>
      <HelpSection title={t('user.guest.sections.help.accessSecurityTitle')}>
        {t('user.guest.sections.accessSecuritySubtitle')}
      </HelpSection>
    </HelpPopover>
  );

  return (
    <>
      <AccordionSection
        title={t('user.guest.sections.accessSecurity')}
        titleAccessory={helpAccessory}
        icon={Shield}
        isExpanded={expanded}
        onToggle={() => setExpanded((prev) => !prev)}
        badge={
          showErrorChip || durationBadgeLabel ? (
            <SectionHeaderActions>
              {showErrorChip && <SectionErrorChip />}
              {durationBadgeLabel && (
                <SectionHeaderChip variant="neutral">{durationBadgeLabel}</SectionHeaderChip>
              )}
            </SectionHeaderActions>
          ) : undefined
        }
      >
        <div className="space-y-4">
          {loadError !== null && (
            <ErrorBlock
              title={t('user.guest.errors.loadSessionDuration')}
              message={loadError}
              retryLabel={t('common.retry')}
              onRetry={() => void fetchGuestDuration()}
            />
          )}
          {/* With nothing read yet the row has no value to show, so the box stands alone */}
          {(state !== null || loadError === null) && (
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
                              <LoadingSpinner
                                inline
                                size="sm"
                                className="user-settings-inline-spinner"
                              />
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
                          color="secondary"
                          size="md"
                          /* Matches the duration dropdown beside it: same w-40, and the same height at
                           both tiers. The phone touch floor is carried by the shared button and
                           control-h-md rules, so the pair no longer needs a per-tier height here. */
                          className="w-40"
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
          )}
        </div>
      </AccordionSection>
    </>
  );
};

export default AccessSecurityCard;

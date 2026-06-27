import { useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';
import Badge from '@components/ui/Badge';
import { Button } from '@components/ui/Button';
import LoadingSpinner from '@components/common/LoadingSpinner';
import { SteamAuthModal } from '@components/modals/auth/SteamAuthModal';
import { useSteamAuthentication } from '@hooks/useSteamAuthentication';
import { useSteamAuth } from '@contexts/useSteamAuth';
import { formatDateTime } from '@utils/formatters';
import { SCHEDULED_PREFILL_ACCOUNT_SERVICE_IDS } from './constants';
import type { ScheduledPrefillAccountServiceId, ScheduledPrefillAuthStatusItem } from './types';

type ScheduledPrefillAuthDisplayServiceId = ScheduledPrefillAccountServiceId | 'battleNet' | 'riot';
type LoginExpiryState = 'normal' | 'soon' | 'expired';

interface ScheduledPrefillAuthStatusProps {
  statuses: ScheduledPrefillAuthStatusItem[];
  loading?: boolean;
  disabled?: boolean;
  onRefresh?: () => void | Promise<void>;
  onError?: (message: string) => void;
  onSuccess?: (message: string) => void;
}

export function ScheduledPrefillAuthStatus({
  statuses,
  loading = false,
  disabled = false,
  onRefresh,
  onError,
  onSuccess
}: ScheduledPrefillAuthStatusProps) {
  const { t } = useTranslation();
  const { refreshSteamAuth, setSteamAuthMode } = useSteamAuth();
  const [showSteamAuthModal, setShowSteamAuthModal] = useState(false);
  const baseKey = 'management.schedules.services.scheduledPrefill.config.auth';

  const statusByService = useMemo(
    () =>
      new Map<ScheduledPrefillAccountServiceId, ScheduledPrefillAuthStatusItem>(
        statuses.map((status) => [status.serviceId, status])
      ),
    [statuses]
  );

  const refreshScheduledAuthStatus = () => {
    if (onRefresh) {
      void Promise.resolve(onRefresh()).catch((error: unknown) => {
        onError?.(error instanceof Error ? error.message : String(error));
      });
    }
  };

  const { state, actions } = useSteamAuthentication({
    onSuccess: (message) => {
      setSteamAuthMode('authenticated');
      setShowSteamAuthModal(false);
      void refreshSteamAuth();
      refreshScheduledAuthStatus();
      onSuccess?.(message);
    },
    onError
  });

  const handleSteamLogin = () => {
    actions.resetAuthForm();
    setShowSteamAuthModal(true);
  };

  const handleCloseSteamAuthModal = () => {
    if (!state.loading) {
      setShowSteamAuthModal(false);
      actions.resetAuthForm();
    }
  };

  const getLoginExpiryState = (expiresAtUtc: string | null): LoginExpiryState | null => {
    if (!expiresAtUtc) return null;
    const expiresAtMs = new Date(expiresAtUtc).getTime();
    if (Number.isNaN(expiresAtMs)) return null;

    const msUntilExpiry = expiresAtMs - Date.now();
    if (msUntilExpiry <= 0) return 'expired';

    const fourteenDaysMs = 14 * 24 * 60 * 60 * 1000;
    return msUntilExpiry <= fourteenDaysMs ? 'soon' : 'normal';
  };

  const getLoginExpiryKey = (expiryState: LoginExpiryState) => {
    if (expiryState === 'expired') return 'loginExpired';
    if (expiryState === 'soon') return 'loginExpiresSoon';
    return 'loginExpires';
  };

  const renderServiceStatus = (serviceId: ScheduledPrefillAuthDisplayServiceId) => {
    if (serviceId === 'battleNet' || serviceId === 'riot') {
      return (
        <Badge variant="success" className="scheduled-prefill-auth-status__chip">
          {t(`${baseKey}.states.noLoginRequired`)}
        </Badge>
      );
    }

    const status = statusByService.get(serviceId);
    if (!status) {
      return (
        <Badge variant="neutral" className="scheduled-prefill-auth-status__chip">
          {t(`${baseKey}.states.unknown`)}
        </Badge>
      );
    }

    if (status.loginState === 'unsupported') {
      return (
        <Badge variant="warning" className="scheduled-prefill-auth-status__chip">
          {t(`${baseKey}.states.unsupported`)}
        </Badge>
      );
    }

    if (status.loginState === 'loginRequired') {
      return (
        <div className="scheduled-prefill-auth-status__action flex items-center gap-2">
          <Badge variant="warning" className="scheduled-prefill-auth-status__chip">
            {t(`${baseKey}.states.loginRequired`)}
          </Badge>
          {serviceId === 'steam' && (
            <Button
              type="button"
              variant="filled"
              size="sm"
              loading={state.loading}
              disabled={disabled}
              onClick={handleSteamLogin}
            >
              {t(`${baseKey}.actions.logIn`)}
            </Button>
          )}
        </div>
      );
    }

    return (
      <Badge
        variant={status.isAuthenticated ? 'success' : 'neutral'}
        className="scheduled-prefill-auth-status__chip"
      >
        {status.isAuthenticated
          ? t(`${baseKey}.states.ready`)
          : t(`${baseKey}.states.notAuthenticated`)}
      </Badge>
    );
  };

  const serviceIds: ScheduledPrefillAuthDisplayServiceId[] = [
    ...SCHEDULED_PREFILL_ACCOUNT_SERVICE_IDS,
    'battleNet',
    'riot'
  ];

  return (
    <>
      <div className="scheduled-prefill-auth-status themed-card border border-themed-primary rounded-lg p-4">
        <div className="scheduled-prefill-auth-status__header flex items-center justify-between gap-3 mb-4">
          <h4 className="text-sm font-semibold text-themed-primary">{t(`${baseKey}.title`)}</h4>
          {loading && (
            <span className="scheduled-prefill-auth-status__loading flex items-center gap-2 text-xs text-themed-muted">
              <LoadingSpinner inline size="sm" />
              {t(`${baseKey}.loading`)}
            </span>
          )}
        </div>

        <div className="scheduled-prefill-auth-status__list grid gap-3">
          {serviceIds.map((serviceId) => {
            const status =
              serviceId === 'battleNet' || serviceId === 'riot'
                ? null
                : statusByService.get(serviceId);
            const expiryState = status ? getLoginExpiryState(status.expiresAtUtc) : null;
            const expiresAt = status?.expiresAtUtc ? formatDateTime(status.expiresAtUtc) : null;
            const serviceName = t(`${baseKey}.services.${serviceId}`);

            return (
              <div
                key={serviceId}
                className="scheduled-prefill-auth-status__item flex flex-col gap-2 rounded-lg bg-themed-tertiary p-3 sm:flex-row sm:items-center sm:justify-between"
              >
                <div className="scheduled-prefill-auth-status__meta min-w-0">
                  <p className="text-sm font-medium text-themed-primary">
                    {t(`${baseKey}.services.${serviceId}`)}
                  </p>
                  {status?.displayName && (
                    <p className="text-xs text-themed-muted">
                      {t(`${baseKey}.displayName`, { displayName: status.displayName })}
                    </p>
                  )}
                  {expiresAt && expiryState && (
                    <p
                      className={`scheduled-prefill-auth-status__expiry scheduled-prefill-auth-status__expiry--${expiryState}`}
                    >
                      {t(`${baseKey}.${getLoginExpiryKey(expiryState)}`, {
                        date: expiresAt,
                        service: serviceName
                      })}
                    </p>
                  )}
                </div>
                {renderServiceStatus(serviceId)}
              </div>
            );
          })}
        </div>
      </div>

      <SteamAuthModal
        opened={showSteamAuthModal}
        onClose={handleCloseSteamAuthModal}
        state={state}
        actions={actions}
      />
    </>
  );
}

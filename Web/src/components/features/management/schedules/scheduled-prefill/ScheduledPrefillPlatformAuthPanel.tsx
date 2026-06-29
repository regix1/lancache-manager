import { useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';
import Badge from '@components/ui/Badge';
import { Button } from '@components/ui/Button';
import LoadingSpinner from '@components/common/LoadingSpinner';
import { SteamAuthModal } from '@components/modals/auth/SteamAuthModal';
import { ScheduledPrefillEpicAuthButton } from './auth/ScheduledPrefillEpicAuthButton';
import { ScheduledPrefillXboxAuthButton } from './auth/ScheduledPrefillXboxAuthButton';
import { useScheduledPrefillSteamAuth } from '@hooks/useScheduledPrefillSteamAuth';
import { formatDateTime } from '@utils/formatters';
import { SCHEDULED_PREFILL_BUTTON_SIZE } from './constants';
import {
  isScheduledPrefillAccountService,
  isScheduledPrefillAnonymousService
} from './scheduledPrefillPlatformUi';
import type {
  ScheduledPrefillAccountServiceId,
  ScheduledPrefillAuthStatusItem,
  ScheduledPrefillServiceKey
} from './types';

type LoginExpiryState = 'normal' | 'soon' | 'expired';

interface ScheduledPrefillPlatformAuthPanelProps {
  serviceKey: ScheduledPrefillServiceKey;
  statuses: ScheduledPrefillAuthStatusItem[];
  loading?: boolean;
  disabled?: boolean;
  onRefresh?: () => void | Promise<void>;
  onError?: (message: string) => void;
  onSuccess?: (message: string) => void;
}

export function ScheduledPrefillPlatformAuthPanel({
  serviceKey,
  statuses,
  loading = false,
  disabled = false,
  onRefresh,
  onError,
  onSuccess
}: ScheduledPrefillPlatformAuthPanelProps) {
  const { t } = useTranslation();
  const [showSteamAuthModal, setShowSteamAuthModal] = useState(false);
  const baseKey = 'management.schedules.services.scheduledPrefill.config';
  const authKey = `${baseKey}.auth`;

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

  const { state, actions } = useScheduledPrefillSteamAuth({
    onSuccess: (message) => {
      setShowSteamAuthModal(false);
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

  if (isScheduledPrefillAnonymousService(serviceKey)) {
    return (
      <div className="scheduled-prefill-platform-auth">
        <p className="scheduled-prefill-platform-auth__description">
          {t(`${authKey}.anonymous.${serviceKey}.description`)}
        </p>
        <Badge variant="success" className="scheduled-prefill-platform-auth__chip">
          {t(`${authKey}.states.noLoginRequired`)}
        </Badge>
      </div>
    );
  }

  const status = statusByService.get(serviceKey);
  const serviceName = t(`${authKey}.services.${serviceKey}`);
  const expiryState = status ? getLoginExpiryState(status.expiresAtUtc) : null;
  const expiresAt = status?.expiresAtUtc ? formatDateTime(status.expiresAtUtc) : null;
  const needsLogin = status?.loginState === 'loginRequired';
  const subtitle = status?.displayName
    ? t(`${authKey}.displayName`, { displayName: status.displayName })
    : needsLogin
      ? t(`${authKey}.states.loginRequired`)
      : null;

  const renderAction = () => {
    if (!status) {
      return (
        <Badge variant="neutral" className="scheduled-prefill-platform-auth__chip">
          {t(`${authKey}.states.unknown`)}
        </Badge>
      );
    }

    if (status.loginState === 'unsupported') {
      return (
        <Badge variant="warning" className="scheduled-prefill-platform-auth__chip">
          {t(`${authKey}.states.unsupported`)}
        </Badge>
      );
    }

    if (status.loginState === 'loginRequired') {
      if (serviceKey === 'steam') {
        return (
          <Button
            type="button"
            variant="filled"
            size={SCHEDULED_PREFILL_BUTTON_SIZE}
            loading={state.loading}
            disabled={disabled}
            onClick={handleSteamLogin}
          >
            {t(`${authKey}.actions.logIn`)}
          </Button>
        );
      }

      if (serviceKey === 'epic') {
        return (
          <ScheduledPrefillEpicAuthButton
            disabled={disabled}
            onSuccess={(message) => {
              refreshScheduledAuthStatus();
              onSuccess?.(message);
            }}
            onError={onError}
          />
        );
      }

      return (
        <ScheduledPrefillXboxAuthButton
          disabled={disabled}
          onSuccess={(message) => {
            refreshScheduledAuthStatus();
            onSuccess?.(message);
          }}
          onError={onError}
        />
      );
    }

    return (
      <Badge variant="success" className="scheduled-prefill-platform-auth__chip">
        {t(`${authKey}.states.ready`)}
      </Badge>
    );
  };

  if (!isScheduledPrefillAccountService(serviceKey)) {
    return null;
  }

  return (
    <>
      <div className="scheduled-prefill-platform-auth">
        <p className="scheduled-prefill-platform-auth__description">
          {t(`${authKey}.platformHelp.${serviceKey}`)}
        </p>
        {loading ? (
          <div className="scheduled-prefill-platform-auth__state" role="status" aria-live="polite">
            <LoadingSpinner inline size="sm" />
            <span>{t(`${authKey}.loading`)}</span>
          </div>
        ) : !status ? (
          <div className="scheduled-prefill-platform-auth__row">
            <div className="scheduled-prefill-platform-auth__meta">
              <p className="scheduled-prefill-platform-auth__subtitle">
                {t(`${authKey}.states.unknown`)}
              </p>
            </div>
            <div className="scheduled-prefill-platform-auth__action">
              {onRefresh && (
                <Button
                  type="button"
                  variant="default"
                  size={SCHEDULED_PREFILL_BUTTON_SIZE}
                  disabled={disabled}
                  onClick={refreshScheduledAuthStatus}
                >
                  {t('common.refresh')}
                </Button>
              )}
            </div>
          </div>
        ) : (
          <div className="scheduled-prefill-platform-auth__row">
            <div className="scheduled-prefill-platform-auth__meta">
              {subtitle && <p className="scheduled-prefill-platform-auth__subtitle">{subtitle}</p>}
              {expiresAt && expiryState && (
                <p
                  className={`scheduled-prefill-platform-auth__expiry scheduled-prefill-platform-auth__expiry--${expiryState}`}
                >
                  {t(`${authKey}.${getLoginExpiryKey(expiryState)}`, {
                    date: expiresAt,
                    service: serviceName
                  })}
                </p>
              )}
            </div>
            <div className="scheduled-prefill-platform-auth__action">{renderAction()}</div>
          </div>
        )}
      </div>

      {serviceKey === 'steam' && (
        <SteamAuthModal
          opened={showSteamAuthModal}
          onClose={handleCloseSteamAuthModal}
          state={state}
          actions={actions}
        />
      )}
    </>
  );
}

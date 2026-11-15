import React, { useState, type Dispatch, type SetStateAction } from 'react';
import { Lock, Unlock } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { Button } from '@components/ui/Button';
import { ToggleSwitch } from '@components/ui/ToggleSwitch';
import ApiService from '@services/api.service';
import { useAuth } from '@contexts/useAuth';
import { useErrorHandler } from '@/hooks/useErrorHandler';
import type { MetricsSecurityResponse } from './GrafanaEndpoints.types';

interface MetricsAccessSwitchProps {
  security: MetricsSecurityResponse;
  /** The host's setter: each write is newer than any read the host has out. */
  setSecurity: Dispatch<SetStateAction<MetricsSecurityResponse | null>>;
  /** The switch's size: a Users setting row draws its switches at md, the Grafana card at sm. */
  size?: 'sm' | 'md';
}

/**
 * The Public / Secured switch for `/metrics` and its Reset to default. The Grafana card and the
 * Users page sign-in pane both draw this one control, so they save the same setting the same way.
 */
const MetricsAccessSwitch: React.FC<MetricsAccessSwitchProps> = ({
  security,
  setSecurity,
  size = 'sm'
}) => {
  const { t } = useTranslation();
  const { isAdmin, sessionType } = useAuth();
  // The rule answers everyone, so a user-role account, which changes only itself, sees it read-only.
  const mayChange = isAdmin && sessionType !== 'user';
  const { notifyError } = useErrorHandler();
  const [isToggling, setIsToggling] = useState(false);
  const [isResetting, setIsResetting] = useState(false);

  const handleToggleAuth = async (value?: string) => {
    if (isToggling || isResetting) return;
    setIsToggling(true);
    const newValue = value ? value === 'secured' : !security.requiresAuthentication;
    // A push or a newer read replaces this object, so the answer below writes only while the
    // switch still shows this click.
    const optimistic = { ...security, requiresAuthentication: newValue };
    setSecurity(optimistic);
    try {
      const saved = await ApiService.setMetricsSecurity(newValue);
      setSecurity((prev) => (prev === optimistic ? saved : prev));
    } catch (error: unknown) {
      setSecurity((prev) => (prev === optimistic ? security : prev));
      notifyError(t('management.grafana.metricsToggle.error'), error);
    } finally {
      setIsToggling(false);
    }
  };

  const handleResetToDefault = async () => {
    if (isToggling || isResetting) return;
    setIsResetting(true);
    try {
      const saved = await ApiService.setMetricsSecurity(null);
      setSecurity((prev) => (prev === security ? saved : prev));
    } catch (error: unknown) {
      notifyError(t('management.grafana.metricsToggle.error'), error);
    } finally {
      setIsResetting(false);
    }
  };

  return (
    <div className="metrics-toggle-row cluster">
      <ToggleSwitch
        size={size}
        options={[
          {
            value: 'public',
            label: t('management.grafana.publicOption'),
            icon: <Unlock />,
            activeColor: 'default'
          },
          {
            value: 'secured',
            label: t('management.grafana.securedOption'),
            icon: <Lock />,
            activeColor: 'success'
          }
        ]}
        value={security.requiresAuthentication ? 'secured' : 'public'}
        onChange={handleToggleAuth}
        disabled={!mayChange}
        loading={isToggling}
        title={
          !mayChange
            ? t('management.grafana.metricsToggle.adminRequired')
            : security.requiresAuthentication
              ? t('management.grafana.securedTooltip')
              : t('management.grafana.publicTooltip')
        }
      />
      {mayChange && (
        <Button
          variant="filled"
          color="secondary"
          size="sm"
          disabled={security.source !== 'ui'}
          loading={isResetting}
          onClick={handleResetToDefault}
          className="metrics-reset-button"
        >
          {t('management.grafana.metricsToggle.resetToDefault')}
        </Button>
      )}
    </div>
  );
};

export default MetricsAccessSwitch;

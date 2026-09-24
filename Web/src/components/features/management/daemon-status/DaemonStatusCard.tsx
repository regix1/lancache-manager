import React, { useCallback, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { AccordionSection } from '@components/ui/AccordionSection';
import { SectionErrorChip, SectionHeaderChip } from '@components/ui/SectionHeaderActions';
import { Button } from '@components/ui/Button';
import { ErrorBlock } from '@components/ui/ErrorBlock';
import { HelpPopover, HelpSection, HelpNote, HelpDefinition } from '@components/ui/HelpPopover';
import { LoadingState } from '@components/ui/ManagerCard';
import { useAccordionGroupItem } from '@contexts/AccordionGroupContext';
import type { ColorToken } from '@utils/eventColors';
import type { DaemonStatusHelpContent, DaemonStatusIcon } from './daemonStatus.types';

/**
 * Login/logout controls for the authenticated variant of the card. Supplying `auth` is what turns a
 * connectivity-only card into one that can sign in; the login-free services pass a `readout`.
 */
interface DaemonStatusAuth {
  /** The control is an admin affordance and is omitted entirely when false. */
  enabled: boolean;
  loginLabel: string;
  logoutLabel: string;
  onLogin: () => void;
  onLogout: () => void;
  loggingOut: boolean;
  /**
   * Xbox mints an operation id and its own terminal notification per login start, so its button must
   * also block a second click while one attempt owns the flow. Epic's flow has no such cost and
   * leaves these unset.
   */
  loginPending?: boolean;
  loginDisabled?: boolean;
  logoutDisabled?: boolean;
  reason?: string | null;
}

interface DaemonStatusCardProps {
  accordionId: string;
  title: string;
  /** Opening block of the header help popover, above the per-service definitions. */
  description: string;
  icon: DaemonStatusIcon;
  iconColor: ColorToken;
  help: DaemonStatusHelpContent;
  loading: boolean;
  loadingMessage: string;
  /** The failed status read's sentence, `null` when the last read answered. */
  loadError: string | null;
  /** "Failed to load {service} status", the box title. */
  loadErrorTitle: string;
  /** Reads the status again; the box's Retry. */
  onRetry: () => void;
  /** Drives the badge and selects between the connected and disconnected copy. */
  connected: boolean;
  connectedLabel: string;
  notConnectedLabel: string;
  /** Bold first line of the status panel. */
  headline: string;
  /** Muted second line of the status panel. */
  detail: string;
  /** Further muted lines under `detail` — Xbox's Microsoft-account login expiry. */
  extraDetail?: React.ReactNode;
  /** Right-hand readout on cards with no login control, e.g. the active session count. */
  readout?: React.ReactNode;
  auth?: DaemonStatusAuth;
  /** Rendered under the status panel — the per-service mapping table on the authenticated cards. */
  children?: React.ReactNode;
}

/**
 * Shared shell for every per-service daemon card. It owns the accordion wiring, the connected badge,
 * the loading and load-error states and the status panel, so a service contributes only its brand,
 * its copy and either a readout or a login control.
 */
const DaemonStatusCard: React.FC<DaemonStatusCardProps> = ({
  accordionId,
  title,
  description,
  icon,
  iconColor,
  help,
  loading,
  loadingMessage,
  loadError,
  loadErrorTitle,
  onRetry,
  connected,
  connectedLabel,
  notConnectedLabel,
  headline,
  detail,
  extraDetail,
  readout,
  auth,
  children
}) => {
  const { t } = useTranslation();
  const [expanded, setExpanded] = useState(false);
  const toggleExpanded = useCallback(() => setExpanded((prev) => !prev), []);
  useAccordionGroupItem(accordionId, expanded, toggleExpanded);

  const statusBadge = !loading ? (
    connected ? (
      <SectionHeaderChip variant="success">{connectedLabel}</SectionHeaderChip>
    ) : (
      <SectionHeaderChip variant="neutral">{notConnectedLabel}</SectionHeaderChip>
    )
  ) : undefined;

  const helpAccessory = (
    <HelpPopover position="left" width={320}>
      <HelpSection title={t('management.sections.integrations.daemon.help.aboutTitle')}>
        {description}
      </HelpSection>
      <HelpSection title={help.title} variant="subtle">
        <HelpDefinition items={help.definitions} />
      </HelpSection>
      <HelpNote type="info">{help.note}</HelpNote>
    </HelpPopover>
  );

  return (
    <AccordionSection
      title={title}
      titleAccessory={helpAccessory}
      icon={icon}
      iconColor={iconColor}
      isExpanded={expanded}
      onToggle={toggleExpanded}
      // The open card's headline says the same status, so the chip is for the closed card.
      badge={expanded ? undefined : loadError !== null ? <SectionErrorChip /> : statusBadge}
    >
      {loading ? (
        <LoadingState message={loadingMessage} shape="cards" rows={1} />
      ) : loadError !== null ? (
        <ErrorBlock
          title={loadErrorTitle}
          message={loadError}
          retryLabel={t('common.retry')}
          onRetry={onRetry}
        />
      ) : (
        <div className="space-y-3">
          <div className="p-3 rounded-lg bg-themed-tertiary">
            <div className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-4">
              <div className="flex-1 min-w-0">
                <p className="text-themed-primary text-sm font-medium mb-1">{headline}</p>
                <p className="text-xs text-themed-muted">{detail}</p>
                {extraDetail}
                {auth?.reason && (
                  <p className="text-xs text-themed-muted mt-1" role="status">
                    {auth.reason}
                  </p>
                )}
              </div>
              {readout && <div className="flex-shrink-0">{readout}</div>}
              {/* Stacked, the row is a column and the button would sit against the left edge while
                  Steam's card keeps its actions on the right. self-end holds that edge below sm;
                  above it justify-between already does the job. */}
              {auth?.enabled && (
                <div className="flex-shrink-0 self-end sm:self-auto">
                  {connected ? (
                    <Button
                      onClick={auth.onLogout}
                      loading={auth.loggingOut}
                      disabled={auth.logoutDisabled}
                      variant="filled"
                      color="secondary"
                      size="sm"
                    >
                      {auth.logoutLabel}
                    </Button>
                  ) : (
                    <Button
                      onClick={auth.onLogin}
                      loading={auth.loginPending}
                      disabled={auth.loginDisabled}
                      variant="filled"
                      color="primary"
                      size="sm"
                    >
                      {auth.loginLabel}
                    </Button>
                  )}
                </div>
              )}
            </div>
          </div>
        </div>
      )}

      {children && <div className="mt-4">{children}</div>}
    </AccordionSection>
  );
};

export default DaemonStatusCard;

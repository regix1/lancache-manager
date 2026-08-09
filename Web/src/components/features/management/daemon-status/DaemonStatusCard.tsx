import React, { useCallback, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { AccordionSection } from '@components/ui/AccordionSection';
import { SectionHeaderChip } from '@components/ui/SectionHeaderActions';
import { Button } from '@components/ui/Button';
import { HelpPopover, HelpSection, HelpNote, HelpDefinition } from '@components/ui/HelpPopover';
import { LoadingState } from '@components/ui/ManagerCard';
import { useAccordionGroupItem } from '@contexts/AccordionGroupContext';
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
}

interface DaemonStatusCardProps {
  accordionId: string;
  title: string;
  /** Opening block of the header help popover, above the per-service definitions. */
  description: string;
  icon: DaemonStatusIcon;
  iconColor: string;
  help: DaemonStatusHelpContent;
  loading: boolean;
  loadingMessage: string;
  hasError: boolean;
  errorMessage: string;
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
  hasError,
  errorMessage,
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
      badge={statusBadge}
    >
      {loading ? (
        <LoadingState message={loadingMessage} shape="cards" rows={1} />
      ) : (
        <>
          {hasError && (
            <div className="p-2 mb-2 rounded-lg bg-themed-warning text-themed-warning text-xs">
              {errorMessage}
            </div>
          )}

          <div className="p-3 rounded-lg bg-themed-tertiary">
            <div className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-4">
              <div className="flex-1 min-w-0">
                <p className="text-themed-primary text-sm font-medium mb-1">{headline}</p>
                <p className="text-xs text-themed-muted">{detail}</p>
                {extraDetail}
              </div>
              {readout && <div className="flex-shrink-0">{readout}</div>}
              {auth?.enabled && (
                <div className="flex-shrink-0">
                  {connected ? (
                    <Button
                      onClick={auth.onLogout}
                      loading={auth.loggingOut}
                      variant="filled"
                      color="red"
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
                      color="blue"
                      size="sm"
                    >
                      {auth.loginLabel}
                    </Button>
                  )}
                </div>
              )}
            </div>
          </div>
        </>
      )}

      {children && <div className="mt-4">{children}</div>}
    </AccordionSection>
  );
};

export default DaemonStatusCard;

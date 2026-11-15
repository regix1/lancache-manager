import React, { useEffect, useState } from 'react';
import { useTranslation } from 'react-i18next';
import StatusDot from '@components/common/StatusDot';
import { Button } from '@components/ui/Button';
import { ErrorBlock } from '@components/ui/ErrorBlock';
import { ClientIpDisplay } from '@components/ui/ClientIpDisplay';
import { RowActionsMenu } from '@components/ui/RowActionsMenu';
import { ActionMenuItem, ActionMenuDivider } from '@components/ui/ActionMenu';
import { isAccountHolder } from '@services/auth.service';
import { useAuth } from '@contexts/useAuth';
import { useSessionPreferences } from '@contexts/useSessionPreferences';
import { useErrorHandler } from '@hooks/useErrorHandler';
import { useCopyFeedback } from '@hooks/useCopyFeedback';
import { useFormattedDateTime } from '@hooks/useFormattedDateTime';
import { copyText } from '@utils/clipboard';
import {
  type AccountActions,
  type Session,
  type ThemeOption,
  type UserAccount,
  cleanIpAddress,
  deviceTitle,
  formatLocation,
  sessionName
} from './types';
import { formatLastSeen, formatTimeLeft } from './sessionTime';
import type { useUserSessions } from './useUserSessions';
import type { GuestThreadDefaults } from './useGuestThreadDefaults';
import UserPaneHeader from './UserPaneHeader';
import UserPaneSection from './UserPaneSection';
import UserListRow from './UserListRow';
import SessionPreferences from './SessionPreferences';

interface DeviceFact {
  key: string;
  label: string;
  value: React.ReactNode;
}

interface SessionDetailProps {
  session: Session;
  account: UserAccount | null;
  otherSessions: Session[];
  availableThemes: ThemeOption[];
  defaultGuestTheme: string;
  defaultGuestRefreshRate: string;
  guestRefreshRateLocked: boolean;
  guestDefaultsLoaded: boolean;
  // The guest-default reads failed: the Theme and Refresh rate rows compare against those defaults
  // and stay hidden until they load, so the error and its Retry sit above the preference rows.
  guestDefaultsError: string | null;
  onRetryGuestDefaults: () => Promise<void>;
  guestThreadDefaults: GuestThreadDefaults;
  accountActions: AccountActions;
  getSessionStatus: ReturnType<typeof useUserSessions>['getSessionStatus'];
  onPickSession: (sessionId: string) => void;
  onRevoke: (session: Session) => void;
  onDelete: (session: Session) => void;
}

const SessionDetail: React.FC<SessionDetailProps> = ({
  session,
  account,
  otherSessions,
  availableThemes,
  defaultGuestTheme,
  defaultGuestRefreshRate,
  guestRefreshRateLocked,
  guestDefaultsLoaded,
  guestDefaultsError,
  onRetryGuestDefaults,
  guestThreadDefaults,
  accountActions,
  getSessionStatus,
  onPickSession,
  onRevoke,
  onDelete
}) => {
  const { t } = useTranslation();
  const { accountId, authenticationEnabled, refreshAuth, logout, sessionType } = useAuth();
  const { notifyError } = useErrorHandler();
  const { getSessionPreferences, loadSessionPreferences, isLoaded, isLoading } =
    useSessionPreferences();
  const [loggingOut, setLoggingOut] = useState(false);
  const [menuOpen, setMenuOpen] = useState(false);
  const [copied, markCopied] = useCopyFeedback(false);

  const active = !session.isRevoked && !session.isExpired;
  const guest = session.sessionType === 'guest';
  const createdText = useFormattedDateTime(session.createdAt);
  const expiresText = useFormattedDateTime(session.expiresAt);
  const endedText = useFormattedDateTime(session.revokedAt ?? session.expiresAt);
  const accountCreatedText = useFormattedDateTime(account?.createdAtUtc);
  const accountLastSignInText = useFormattedDateTime(account?.lastLoginAtUtc);

  // The clock the session reads by comes from its preferences, which ended sessions no longer load.
  useEffect(() => {
    if (!active || isLoaded(session.id) || isLoading(session.id)) return;
    void loadSessionPreferences(session.id);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [active, session.id]);
  const clockPrefs = active ? getSessionPreferences(session.id) : null;

  const deviceLine = (target: Session): string => {
    const place = formatLocation(target.city, target.regionName, target.countryName);
    const device = deviceTitle(target.userAgent, t);
    return place ? t('user.panes.facts.deviceAt', { device, place }) : device;
  };

  // A signed-out session is also revoked, so signedOut is checked first.
  const endedReason = session.signedOut
    ? t('user.panes.list.endedSignedOut', { when: endedText })
    : session.isRevoked
      ? t('user.panes.list.endedRevoked', { when: endedText })
      : t('user.panes.list.endedExpired', { when: endedText });

  const headerLines = (): string[] => {
    if (!active) {
      return [
        t('user.panes.header.endedLine', {
          kind: guest ? t('user.panes.header.kindGuest') : t('user.panes.header.kindAccount'),
          reason: endedReason
        })
      ];
    }
    if (guest) {
      // The header names the place by city and region; the Device section below adds the country.
      const place = formatLocation(session.city, session.regionName, null);
      const device = deviceTitle(session.userAgent, t);
      return [
        t('user.panes.header.guestSession', {
          device: place ? t('user.panes.facts.deviceAt', { device, place }) : device,
          remaining: formatTimeLeft(session.expiresAt, t)
        })
      ];
    }
    if (account === null) return [];
    const role = account.isMainAdmin ? t('user.accounts.mainAdmin') : t('user.accounts.roles.user');
    const created = t('user.panes.header.created', { when: accountCreatedText });
    return [
      t('user.panes.header.accountLine', {
        role,
        state: t('user.panes.list.signedInOn', { count: otherSessions.length + 1 })
      }),
      account.lastLoginAtUtc
        ? `${created}${t('user.panes.separator')}${t('user.panes.header.lastSignIn', { when: accountLastSignInText })}`
        : created
    ];
  };

  const handleLogout = async (): Promise<void> => {
    if (loggingOut) return;
    try {
      setLoggingOut(true);
      await logout();
      await refreshAuth();
    } catch (err: unknown) {
      notifyError(t('activeSessions.errors.logout'), err, { logLabel: 'Failed to log out' });
    } finally {
      setLoggingOut(false);
    }
  };

  const handleCopyId = async (): Promise<void> => {
    if (await copyText(session.id)) markCopied(true);
  };

  // The primary admin cannot be edited, disabled or deleted, and nobody disables or deletes the
  // account they are signed in with, so those items are left out rather than shown disabled.
  // A user-role account changes only its own account's sessions; the server refuses the rest.
  const mayChange = sessionType !== 'user' || session.accountId === accountId;
  const accountItems = account !== null && !account.isMainAdmin && mayChange ? account : null;
  const canRemoveAccount = accountItems !== null && accountItems.id !== accountId;
  // Revoke and Delete session share this rule. The shared session is remade on the next request while
  // sign-in is off, so it offers neither.
  const canDeleteSession =
    mayChange && active && !session.isCurrentSession && !session.isSharedSession;
  const name = sessionName(session, t);

  // UTC is a third clock rather than a variation on local-vs-server, so it is tested first.
  const clockWords =
    clockPrefs === null
      ? null
      : clockPrefs.useUtcTimezone
        ? t('user.panes.facts.clockUtc')
        : clockPrefs.useLocalTimezone
          ? t('user.panes.facts.clockLocal')
          : t('user.panes.facts.clockServer');

  // Facts are grouped where, when and browser so each row holds one kind; a fact the session did not
  // report is left out, not shown empty.
  const whereCells: (DeviceFact | null)[] = [
    { key: 'device', label: t('user.panes.facts.labels.device'), value: deviceLine(session) },
    session.ipAddress
      ? {
          key: 'address',
          label: t('user.panes.facts.labels.address'),
          value: <ClientIpDisplay clientIp={cleanIpAddress(session.ipAddress)} />
        }
      : null,
    session.publicIpAddress
      ? {
          key: 'publicAddress',
          label: t('user.panes.facts.labels.publicAddress'),
          value: cleanIpAddress(session.publicIpAddress)
        }
      : null,
    session.ispName
      ? { key: 'provider', label: t('user.panes.facts.labels.provider'), value: session.ispName }
      : null
  ];
  const whenCells: (DeviceFact | null)[] = [
    { key: 'signedIn', label: t('user.panes.facts.signedIn'), value: createdText },
    active
      ? {
          key: 'lastSeen',
          label: t('user.panes.facts.labels.lastSeen'),
          value: formatLastSeen(session.lastSeenAt, t)
        }
      : null,
    active
      ? {
          key: 'expires',
          label: t('user.panes.facts.labels.expires'),
          value: isAccountHolder(session.sessionType ?? null)
            ? t('user.panes.facts.never')
            : expiresText
        }
      : { key: 'ended', label: t('user.panes.facts.labels.ended'), value: endedReason }
  ];
  const browserCells: (DeviceFact | null)[] = [
    session.browserLanguage
      ? {
          key: 'language',
          label: t('user.panes.facts.labels.language'),
          value: session.browserLanguage
        }
      : null,
    session.timezone
      ? { key: 'timezone', label: t('user.panes.facts.labels.timezone'), value: session.timezone }
      : null,
    session.screenResolution
      ? {
          key: 'screen',
          label: t('user.panes.facts.labels.screen'),
          value: session.screenResolution
        }
      : null,
    clockWords !== null
      ? { key: 'clock', label: t('user.panes.facts.labels.clock'), value: clockWords }
      : null
  ];
  const factGroups = [whereCells, whenCells, browserCells]
    .map((cells: (DeviceFact | null)[]) =>
      cells.filter((fact: DeviceFact | null): fact is DeviceFact => fact !== null)
    )
    .filter((group: DeviceFact[]) => group.length > 0);

  return (
    <>
      <UserPaneHeader
        title={name}
        lines={headerLines()}
        actions={
          <>
            {/* With authentication off the server keeps the shared session on sign-out, so Log Out
                would do nothing. */}
            {authenticationEnabled && active && session.isCurrentSession && (
              <Button
                variant="default"
                size="sm"
                className="users-pane-btn pointer-target-44"
                onClick={() => void handleLogout()}
                loading={loggingOut}
              >
                {t('activeSessions.actions.logout')}
              </Button>
            )}
            {canDeleteSession && (
              <Button
                variant="default"
                size="sm"
                className="users-pane-btn pointer-target-44"
                onClick={() => onRevoke(session)}
              >
                {t('activeSessions.actions.revoke')}
              </Button>
            )}
            {!active && mayChange && (
              <Button
                variant="default"
                size="sm"
                className="users-pane-btn pointer-target-44"
                onClick={() => onDelete(session)}
              >
                {t('activeSessions.actions.delete')}
              </Button>
            )}
            {(accountItems !== null || canDeleteSession) && (
              <RowActionsMenu
                open={menuOpen}
                onOpenChange={setMenuOpen}
                width="w-60"
                aria-label={t('user.panes.menu.label', { name })}
              >
                {(close: () => void) => (
                  <>
                    {accountItems !== null && (
                      <ActionMenuItem
                        onClick={() => {
                          close();
                          accountActions.onEdit(accountItems);
                        }}
                      >
                        {t('user.panes.menu.editAccount')}
                      </ActionMenuItem>
                    )}
                    {canRemoveAccount && accountItems !== null && (
                      <>
                        <ActionMenuItem
                          disabled={accountActions.disablingAccountId === accountItems.id}
                          onClick={() => {
                            close();
                            accountActions.onSetDisabled(accountItems, !accountItems.isDisabled);
                          }}
                        >
                          {accountItems.isDisabled
                            ? t('user.panes.menu.enableAccount')
                            : t('user.panes.menu.disableAccount')}
                        </ActionMenuItem>
                        <ActionMenuItem
                          onClick={() => {
                            close();
                            accountActions.onDelete(accountItems);
                          }}
                        >
                          {t('user.panes.menu.deleteAccount')}
                        </ActionMenuItem>
                      </>
                    )}
                    {accountItems !== null && canDeleteSession && <ActionMenuDivider />}
                    {canDeleteSession && (
                      <ActionMenuItem
                        onClick={() => {
                          close();
                          onDelete(session);
                        }}
                      >
                        {t('user.panes.menu.deleteSession')}
                      </ActionMenuItem>
                    )}
                  </>
                )}
              </RowActionsMenu>
            )}
          </>
        }
      />

      <div className="users-pane-body">
        <UserPaneSection
          title={
            session.isCurrentSession
              ? t('user.panes.sections.thisDevice')
              : t('user.panes.sections.device')
          }
        >
          {factGroups.map((group: DeviceFact[]) => (
            <dl key={group[0].key} className="users-facts">
              {group.map((fact: DeviceFact) => (
                <div key={fact.key} className="users-facts__item">
                  <dt>{fact.label}</dt>
                  <dd>{fact.value}</dd>
                </div>
              ))}
            </dl>
          ))}
          <dl className="users-facts">
            <div className="users-facts__item users-facts__item--wide">
              <dt>{t('user.panes.facts.labels.session')}</dt>
              <dd className="users-copy-line">
                <span className="users-copy-line__text users-facts__id">{session.id}</span>
                <Button
                  variant="default"
                  size="xs"
                  className="pointer-target-44"
                  onClick={() => void handleCopyId()}
                  aria-label={t('user.panes.facts.copyAria')}
                >
                  <span className="copy-button-label">
                    <span>{copied ? t('user.panes.facts.copied') : t('common.copy')}</span>
                    <span aria-hidden="true">{t('user.panes.facts.copied')}</span>
                  </span>
                </Button>
              </dd>
            </div>
          </dl>
        </UserPaneSection>

        {active && mayChange && guestDefaultsError !== null && (
          <ErrorBlock
            title={t('user.guest.errors.loadDefaults')}
            message={guestDefaultsError}
            retryLabel={t('common.retry')}
            onRetry={onRetryGuestDefaults}
          />
        )}

        {active && mayChange && (
          <SessionPreferences
            key={session.id}
            session={session}
            availableThemes={availableThemes}
            defaultGuestTheme={defaultGuestTheme}
            defaultGuestRefreshRate={defaultGuestRefreshRate}
            guestRefreshRateLocked={guestRefreshRateLocked}
            guestDefaultsLoaded={guestDefaultsLoaded}
            guestThreadDefaults={guestThreadDefaults}
          />
        )}

        {/* Last, so a long list does not push the preferences below the fold. */}
        {otherSessions.length > 0 && (
          <UserPaneSection title={t('user.panes.sections.otherDevices')}>
            <ul className="users-rows">
              {otherSessions.map((other: Session) => {
                const status = getSessionStatus(other);
                const when = formatLastSeen(other.lastSeenAt, t);
                return (
                  <UserListRow
                    key={other.id}
                    lead={<StatusDot state={status} label={t(`activeSessions.status.${status}`)} />}
                    title={deviceTitle(other.userAgent, t)}
                    line={
                      other.city
                        ? t('user.panes.facts.placeSeen', { place: other.city, when })
                        : t('user.panes.facts.lastSeen', { when })
                    }
                    selected={false}
                    onSelect={() => onPickSession(other.id)}
                  />
                );
              })}
            </ul>
          </UserPaneSection>
        )}
      </div>
    </>
  );
};

export default SessionDetail;

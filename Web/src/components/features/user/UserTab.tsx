import React, { useCallback, useEffect, useLayoutEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import {
  ChevronLeft,
  DoorOpen,
  History,
  KeyRound,
  SearchX,
  UserCog,
  UserRound,
  Users
} from 'lucide-react';
import ApiService from '@services/api.service';
import themeService from '@services/theme.service';
import { getErrorMessage } from '@utils/error';
import { storage } from '@utils/storage';
import { useReconnectRefetch } from '@hooks/useReconnectRefetch';
import { useIsDesktop } from '@hooks/useMediaQuery';
import { useAuth } from '@contexts/useAuth';
import { useSignalR } from '@contexts/SignalRContext/useSignalR';
import { Card } from '@components/ui/Card';
import { Button } from '@components/ui/Button';
import Badge from '@components/ui/Badge';
import { SegmentedControl } from '@components/ui/SegmentedControl';
import { SearchInput } from '@components/ui/SearchInput';
import { CustomScrollbar } from '@components/ui/CustomScrollbar';
import { ErrorBlock } from '@components/ui/ErrorBlock';
import { EmptyState, LoadingState } from '@components/ui/ManagerCard';
import StatusDot from '@components/common/StatusDot';
import GuestConfiguration from './GuestConfiguration';
import SignInPane from './SignInPane';
import SessionDetail from './SessionDetail';
import SessionDialogs from './SessionDialogs';
import AccountDetail from './AccountDetail';
import AccountDialogs from './AccountDialogs';
import UserListRow from './UserListRow';
import { useUserSessions } from './useUserSessions';
import { useUserAccounts } from './useUserAccounts';
import { useGuestThreadDefaults } from './useGuestThreadDefaults';
import { formatLastSeen, formatTimeLeft } from './sessionTime';
import {
  type AccountActions,
  type AccountConfirmation,
  type AccountEditor,
  type GuestDurationResponse,
  type Session,
  type ThemeOption,
  type UserAccount,
  cleanIpAddress,
  deviceTitle,
  parseUserAgent,
  sessionName
} from './types';

type ListFilter = 'signedIn' | 'accounts' | 'guests' | 'ended';

const LIST_FILTERS: ListFilter[] = ['signedIn', 'accounts', 'guests', 'ended'];

/**
 * What the detail pane shows. A `session` pick covers active and ended sessions. An `account` pick
 * keeps the session it opened on, so the pane never swaps sessions under itself while that one is
 * still signed in. It is null while the account has none, and takes the first one that appears.
 */
type UserPick =
  | { kind: 'session'; id: string }
  | { kind: 'account'; id: string; sessionId: string | null }
  | { kind: 'guestAccess' }
  | { kind: 'signIn' };

const PICK_STORAGE_KEY = 'lancache_users_pick';

const isUserPick = (value: unknown): value is UserPick => {
  if (typeof value !== 'object' || value === null || !('kind' in value)) return false;
  const pick = value as { kind: unknown; id?: unknown; sessionId?: unknown };
  switch (pick.kind) {
    case 'session':
      return typeof pick.id === 'string';
    case 'account':
      return (
        typeof pick.id === 'string' &&
        (typeof pick.sessionId === 'string' || pick.sessionId === null)
      );
    case 'guestAccess':
    case 'signIn':
      return true;
    default:
      return false;
  }
};

// A stored value written by an older page, or edited by hand, reads as no pick.
const readStoredPick = (): UserPick | null => {
  const stored = storage.getJSON<unknown>(PICK_STORAGE_KEY);
  return isUserPick(stored) ? stored : null;
};

const isGuestSession = (session: Session): boolean => session.sessionType === 'guest';

const sessionMatchesSearch = (session: Session, query: string, deviceLabel: string): boolean => {
  const normalized = query.trim().toLowerCase();
  if (!normalized) return true;

  const parsedUA = parseUserAgent(session.userAgent);
  const ip = session.ipAddress ? cleanIpAddress(session.ipAddress) : '';
  const haystack = [
    deviceLabel,
    parsedUA.title,
    parsedUA.browser,
    parsedUA.os,
    session.userAgent ?? '',
    session.username ?? '',
    session.ipAddress ?? '',
    ip,
    session.publicIpAddress ?? '',
    session.city ?? ''
  ]
    .join(' ')
    .toLowerCase();

  return haystack.includes(normalized);
};

const UserTab: React.FC = () => {
  const { t } = useTranslation();
  const { accountId, isMainAdmin, authenticationEnabled, accountMode, sessionType } = useAuth();
  const isDesktop = useIsDesktop();
  const sessionsState = useUserSessions();
  const accountsState = useUserAccounts();
  const { sessions, historySessions, getSessionStatus } = sessionsState;
  const { accounts } = accountsState;

  const [guestDuration, setGuestDuration] = useState<GuestDurationResponse | null>(null);
  const [guestModeLocked, setGuestModeLocked] = useState<boolean>(false);
  const [defaultGuestTheme, setDefaultGuestTheme] = useState<string>('dark-default');
  const [availableThemes, setAvailableThemes] = useState<ThemeOption[]>([]);
  const [defaultGuestRefreshRate, setDefaultGuestRefreshRate] = useState<string>('STANDARD');
  const [guestRefreshRateLocked, setGuestRefreshRateLocked] = useState<boolean>(true);

  const [guestDefaultsError, setGuestDefaultsError] = useState<string | null>(null);
  const [guestDefaultsLoaded, setGuestDefaultsLoaded] = useState(false);
  const [loadingGuestDefaults, setLoadingGuestDefaults] = useState(false);

  const [filter, setFilter] = useState<ListFilter>('signedIn');
  const [query, setQuery] = useState('');
  const [pick, setPick] = useState<UserPick | null>(readStoredPick);
  const [phoneView, setPhoneView] = useState<'list' | 'detail'>('list');
  const [revoking, setRevoking] = useState<Session | null>(null);
  const [deleting, setDeleting] = useState<Session | null>(null);
  const [editor, setEditor] = useState<AccountEditor | null>(null);
  const [confirmation, setConfirmation] = useState<AccountConfirmation | null>(null);

  const guestDefaultsRequestRef = useRef(0);
  // Pushes received per setting. The server announces a save before it answers it, so a save's reply
  // applies only when no push for its setting arrived while it was open.
  const guestDefaultPushesRef = useRef({
    duration: 0,
    guestLock: 0,
    theme: 0,
    refreshRate: 0,
    refreshRateLock: 0
  });

  const loadGuestDefaults = useCallback(async () => {
    // Mount, reconnect, the duration push and Retry can overlap; only the newest request writes
    // values or the error.
    const request = ++guestDefaultsRequestRef.current;
    // A push received while the reads are out is newer than what they read.
    const pushesBefore = { ...guestDefaultPushesRef.current };
    setLoadingGuestDefaults(true);
    try {
      const [guestConfig, duration, guestTheme, guestRefreshRate, themeResult] = await Promise.all([
        ApiService.getGuestConfig<{ isLocked: boolean }>(),
        ApiService.getGuestSessionDuration(),
        ApiService.getGuestThemePreference<{ themeId: string }>(),
        ApiService.getDefaultGuestRefreshRate<{ refreshRate: string; locked: boolean }>(),
        themeService.loadThemes()
      ]);
      if (request !== guestDefaultsRequestRef.current) return;
      const pushes = guestDefaultPushesRef.current;
      if (pushes.duration === pushesBefore.duration) setGuestDuration(duration);
      if (pushes.guestLock === pushesBefore.guestLock) setGuestModeLocked(guestConfig.isLocked);
      if (pushes.theme === pushesBefore.theme) setDefaultGuestTheme(guestTheme.themeId);
      if (pushes.refreshRate === pushesBefore.refreshRate)
        setDefaultGuestRefreshRate(guestRefreshRate.refreshRate);
      if (pushes.refreshRateLock === pushesBefore.refreshRateLock)
        setGuestRefreshRateLocked(guestRefreshRate.locked);
      setAvailableThemes(
        themeResult.themes.map((theme) => ({ id: theme.meta.id, name: theme.meta.name }))
      );
      setGuestDefaultsError(themeResult.loadError);
      setGuestDefaultsLoaded(true);
      setLoadingGuestDefaults(false);
    } catch (err) {
      if (request !== guestDefaultsRequestRef.current) return;
      setGuestDefaultsError(getErrorMessage(err));
      setLoadingGuestDefaults(false);
    }
  }, []);

  // The Guest access pane's Save calls these, one per changed setting, and shows a rejection as the
  // failure of that setting. null puts the length back to the config file's value.
  const handleUpdateDuration = async (hours: number | null): Promise<void> => {
    const pushesBefore = guestDefaultPushesRef.current.duration;
    const response = await ApiService.setGuestSessionDuration(hours);
    if (guestDefaultPushesRef.current.duration === pushesBefore) setGuestDuration(response);
  };

  const handleToggleGuestLock = async (locked: boolean): Promise<void> => {
    const pushesBefore = guestDefaultPushesRef.current.guestLock;
    await ApiService.setGuestConfigLock(locked);
    if (guestDefaultPushesRef.current.guestLock === pushesBefore) setGuestModeLocked(locked);
  };

  const handleUpdateGuestTheme = async (newThemeId: string): Promise<void> => {
    const pushesBefore = guestDefaultPushesRef.current.theme;
    await ApiService.setGuestThemePreference(newThemeId);
    if (guestDefaultPushesRef.current.theme === pushesBefore) setDefaultGuestTheme(newThemeId);
  };

  const handleUpdateGuestRefreshRate = async (newRate: string): Promise<void> => {
    const pushesBefore = guestDefaultPushesRef.current.refreshRate;
    await ApiService.setDefaultGuestRefreshRate(newRate);
    if (guestDefaultPushesRef.current.refreshRate === pushesBefore)
      setDefaultGuestRefreshRate(newRate);
  };

  const handleUpdateGuestRefreshRateLock = async (locked: boolean): Promise<void> => {
    const pushesBefore = guestDefaultPushesRef.current.refreshRateLock;
    await ApiService.setGuestRefreshRateLock(locked);
    if (guestDefaultPushesRef.current.refreshRateLock === pushesBefore)
      setGuestRefreshRateLocked(locked);
  };

  // SignalR handlers for live config updates
  const { on, off, isConnected } = useSignalR();

  const handleGuestModeLockChanged = useCallback((data: { isLocked: boolean }) => {
    guestDefaultPushesRef.current.guestLock += 1;
    setGuestModeLocked(data.isLocked);
  }, []);

  // The push carries only the hours; the re-read brings the source that goes with them.
  const handleGuestDurationUpdated = useCallback(
    (data: { durationHours: number }) => {
      guestDefaultPushesRef.current.duration += 1;
      setGuestDuration((prev: GuestDurationResponse | null) =>
        prev ? { ...prev, durationHours: data.durationHours } : prev
      );
      void loadGuestDefaults();
    },
    [loadGuestDefaults]
  );

  const handleDefaultGuestThemeChanged = useCallback((data: { newThemeId: string }) => {
    guestDefaultPushesRef.current.theme += 1;
    setDefaultGuestTheme(data.newThemeId);
  }, []);

  const handleDefaultGuestRefreshRateChanged = useCallback((data: { refreshRate: string }) => {
    guestDefaultPushesRef.current.refreshRate += 1;
    setDefaultGuestRefreshRate(data.refreshRate);
  }, []);

  const handleGuestRefreshRateLockChanged = useCallback((data: { locked: boolean }) => {
    guestDefaultPushesRef.current.refreshRateLock += 1;
    setGuestRefreshRateLocked(data.locked);
  }, []);

  // Refresh guest defaults when SignalR reconnects (catches config events missed during disconnect)
  useReconnectRefetch(isConnected, () => void loadGuestDefaults());

  useEffect(() => {
    void loadGuestDefaults();

    on('GuestModeLockChanged', handleGuestModeLockChanged);
    on('GuestDurationUpdated', handleGuestDurationUpdated);
    on('DefaultGuestThemeChanged', handleDefaultGuestThemeChanged);
    on('DefaultGuestRefreshRateChanged', handleDefaultGuestRefreshRateChanged);
    on('GuestRefreshRateLockChanged', handleGuestRefreshRateLockChanged);

    return () => {
      off('GuestModeLockChanged', handleGuestModeLockChanged);
      off('GuestDurationUpdated', handleGuestDurationUpdated);
      off('DefaultGuestThemeChanged', handleDefaultGuestThemeChanged);
      off('DefaultGuestRefreshRateChanged', handleDefaultGuestRefreshRateChanged);
      off('GuestRefreshRateLockChanged', handleGuestRefreshRateLockChanged);
    };
  }, [
    on,
    off,
    loadGuestDefaults,
    handleGuestModeLockChanged,
    handleGuestDurationUpdated,
    handleDefaultGuestThemeChanged,
    handleDefaultGuestRefreshRateChanged,
    handleGuestRefreshRateLockChanged
  ]);

  // ============================================================
  // Rows and pick
  // ============================================================

  // The Sign-in entry is for the primary admin, or for anyone while sign-in is off.
  const signInVisible = isMainAdmin || !authenticationEnabled;
  // Guest settings change every guest, which a user-role account may not do.
  const guestAccessVisible = sessionType !== 'user';
  // A list is judged only once it has loaded and no first load is under way: before that its rows
  // are empty because they have not arrived, not because the pick's target is gone.
  const sessionsReady = sessionsState.loaded && !sessionsState.loading;
  const accountsReady = accountsState.loaded && !accountsState.loading;

  // Two sessions are the same account's only by account id; guests and shared access have none.
  const activeSessionsOf = (id: string): Session[] =>
    sessions.filter((session: Session) => session.accountId === id);

  const ownSession = sessions.find((session: Session) => session.isCurrentSession);
  // Your own session, or Guest access when you have none. Unknown while the first load is out.
  const defaultPick: UserPick | null = ownSession
    ? { kind: 'session', id: ownSession.id }
    : sessionsState.loading && !sessionsState.loaded
      ? null
      : { kind: 'guestAccess' };
  const shownPick = pick ?? defaultPick;

  // A phone hides the list while a pane covers it, which loses the page's scroll; going back to the
  // list puts the scroll where it was.
  const listScrollRef = useRef<number | null>(null);
  // Set by a tap, so only a pick the user made opens at the page's top; a pane that switches on its
  // own (an account signing in, its session ending) leaves the page where the reader has it.
  const openAtTopRef = useRef(false);

  const choose = (next: UserPick): void => {
    setPick(next);
    storage.setJSON(PICK_STORAGE_KEY, next);
    if (!isDesktop && phoneView === 'list') listScrollRef.current = globalThis.scrollY;
    openAtTopRef.current = !isDesktop;
    setPhoneView('detail');
  };

  // A stored pick is judged against the lists only after the loaders that own its kind have each
  // finished one load without error; until then it is kept as it is, stored value included.
  useEffect(() => {
    if (pick === null) return;

    const fallBack = (): void => {
      setPick(null);
      storage.removeItem(PICK_STORAGE_KEY);
      setPhoneView('list');
    };

    switch (pick.kind) {
      case 'guestAccess':
        if (!guestAccessVisible) fallBack();
        return;
      case 'signIn':
        if (!signInVisible) fallBack();
        return;
      case 'session':
        if (!sessionsReady) return;
        // An ended session stays picked and reads as ended; only a deleted one falls back.
        if (
          !sessions.some((session: Session) => session.id === pick.id) &&
          !historySessions.some((session: Session) => session.id === pick.id)
        ) {
          fallBack();
        }
        return;
      case 'account': {
        if (!accountsReady || !sessionsReady) return;
        if (!accounts.some((account: UserAccount) => account.id === pick.id)) {
          fallBack();
          return;
        }
        const live = activeSessionsOf(pick.id);
        if (pick.sessionId === null) {
          // An account that signs in while its pane is open shows that session and its preferences.
          if (live.length > 0) {
            const next: UserPick = { ...pick, sessionId: live[0].id };
            setPick(next);
            storage.setJSON(PICK_STORAGE_KEY, next);
          }
          return;
        }
        if (live.some((session: Session) => session.id === pick.sessionId)) return;
        // The session it opened on has ended: move to the account's next one, or to the account.
        if (sessionsState.loadError !== null) return;
        const next: UserPick = { ...pick, sessionId: live.length > 0 ? live[0].id : null };
        setPick(next);
        storage.setJSON(PICK_STORAGE_KEY, next);
        return;
      }
    }
    // activeSessionsOf reads `sessions`, which is listed.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [
    pick,
    signInVisible,
    guestAccessVisible,
    sessionsReady,
    accountsReady,
    sessions,
    historySessions,
    accounts,
    sessionsState.loadError
  ]);

  const sessionLine = (session: Session, when: string): string => {
    const device = deviceTitle(session.userAgent, t);
    return session.city
      ? t('user.panes.list.sessionLine', { device, place: session.city, when })
      : t('user.panes.list.sessionLineNoPlace', { device, when });
  };

  const accountLine = (account: UserAccount, signedInCount: number): string =>
    [
      account.isMainAdmin ? t('user.accounts.mainAdmin') : t('user.accounts.roles.user'),
      ...(account.isDisabled ? [t('user.panes.list.disabled')] : []),
      signedInCount > 0
        ? t('user.panes.list.signedInOn', { count: signedInCount })
        : account.lastLoginAtUtc == null
          ? t('user.panes.list.neverSignedIn')
          : t('user.panes.list.lastSignedIn', { when: formatLastSeen(account.lastLoginAtUtc, t) })
    ].join(t('user.panes.separator'));

  const guestSessions = sessions.filter(isGuestSession);
  const counts: Record<ListFilter, number> = {
    signedIn: sessions.length,
    accounts: accounts.length,
    guests: guestSessions.length,
    ended: historySessions.length
  };
  const filterLabels: Record<ListFilter, string> = {
    signedIn: t('user.panes.list.filters.signedIn'),
    accounts: t('activeSessions.accountFilter'),
    guests: t('user.panes.list.filters.guests'),
    ended: t('user.panes.list.filters.ended')
  };

  const matchingSessions = (list: Session[]): Session[] =>
    list.filter((session: Session) =>
      sessionMatchesSearch(session, query, deviceTitle(session.userAgent, t))
    );

  const activeRow = (session: Session): React.ReactNode => {
    const status = getSessionStatus(session);
    return (
      <UserListRow
        key={session.id}
        lead={<StatusDot state={status} label={t(`activeSessions.status.${status}`)} />}
        title={sessionName(session, t)}
        aside={session.isCurrentSession ? t('activeSessions.currentSessionShort') : undefined}
        line={sessionLine(
          session,
          isGuestSession(session)
            ? formatTimeLeft(session.expiresAt, t)
            : formatLastSeen(session.lastSeenAt, t)
        )}
        selected={shownPick?.kind === 'session' && shownPick.id === session.id}
        onSelect={() => choose({ kind: 'session', id: session.id })}
      />
    );
  };

  // A session that ended because its user signed out is also revoked, so signedOut is read first.
  const endedRow = (session: Session): React.ReactNode => {
    const when = formatLastSeen(session.revokedAt ?? session.expiresAt, t);
    const ending = session.signedOut
      ? {
          tone: 'idle' as const,
          label: t('activeSessions.status.inactive'),
          text: t('user.panes.list.endedSignedOut', { when })
        }
      : session.isRevoked
        ? {
            tone: 'error' as const,
            label: t('activeSessions.status.revoked'),
            text: t('user.panes.list.endedRevoked', { when })
          }
        : {
            tone: 'warning' as const,
            label: t('activeSessions.prefill.status.expired'),
            text: t('user.panes.list.endedExpired', { when })
          };
    return (
      <UserListRow
        key={session.id}
        lead={<StatusDot tone={ending.tone} label={ending.label} />}
        title={sessionName(session, t)}
        line={sessionLine(session, ending.text)}
        selected={shownPick?.kind === 'session' && shownPick.id === session.id}
        onSelect={() => choose({ kind: 'session', id: session.id })}
      />
    );
  };

  const accountRow = (account: UserAccount): React.ReactNode => {
    const live = activeSessionsOf(account.id);
    const status = live.length > 0 ? getSessionStatus(live[0]) : null;
    return (
      <UserListRow
        key={account.id}
        lead={
          status === null ? (
            <StatusDot tone="idle" label={t('activeSessions.status.inactive')} />
          ) : (
            <StatusDot state={status} label={t(`activeSessions.status.${status}`)} />
          )
        }
        title={account.username}
        aside={account.id === accountId ? t('activeSessions.currentSessionShort') : undefined}
        line={accountLine(account, live.length)}
        selected={shownPick?.kind === 'account' && shownPick.id === account.id}
        dimmed={account.isDisabled}
        onSelect={() =>
          choose({
            kind: 'account',
            id: account.id,
            sessionId: live.length > 0 ? live[0].id : null
          })
        }
      />
    );
  };

  const needle = query.trim().toLowerCase();
  const filterRows = (): { total: number; rows: React.ReactNode[] } => {
    switch (filter) {
      case 'signedIn':
        return { total: sessions.length, rows: matchingSessions(sessions).map(activeRow) };
      case 'guests':
        return {
          total: guestSessions.length,
          rows: matchingSessions(guestSessions).map(activeRow)
        };
      case 'ended':
        return {
          total: historySessions.length,
          rows: matchingSessions(historySessions).map(endedRow)
        };
      case 'accounts':
        return {
          total: accounts.length,
          rows: accounts
            .filter((account: UserAccount) => account.username.toLowerCase().includes(needle))
            .map(accountRow)
        };
    }
  };

  const emptyStates: Record<
    ListFilter,
    { icon: React.ElementType; title: string; subtitle: string }
  > = {
    signedIn: {
      icon: Users,
      title: t('user.panes.list.empty.signedIn'),
      subtitle: t('user.panes.list.empty.signedInHint')
    },
    accounts: {
      icon: UserCog,
      title: t('user.accounts.empty.title'),
      subtitle: t('user.panes.list.empty.accountsHint')
    },
    guests: {
      icon: UserRound,
      title: t('user.panes.list.empty.guests'),
      subtitle: t('user.panes.list.empty.guestsHint')
    },
    ended: {
      icon: History,
      title: t('user.panes.list.empty.ended'),
      subtitle: t('user.panes.list.empty.endedHint')
    }
  };

  // The failed load of one list, with Retry; null while that list has no error.
  const loadErrorBlock = (list: 'sessions' | 'accounts'): React.ReactNode => {
    const loadError = list === 'accounts' ? accountsState.loadError : sessionsState.loadError;
    return loadError === null ? null : (
      <ErrorBlock
        title={
          list === 'accounts'
            ? t('user.accounts.errors.load')
            : t('activeSessions.initialLoadFailed')
        }
        message={loadError}
        retryLabel={t('common.retry')}
        onRetry={() => (list === 'accounts' ? accountsState.reload() : sessionsState.reload(true))}
      />
    );
  };

  const listContent = (): React.ReactNode => {
    const list = filter === 'accounts' ? 'accounts' : 'sessions';
    const owner = filter === 'accounts' ? accountsState : sessionsState;
    const { total, rows } = filterRows();
    // The loaders start loading, so the skeleton is up from the first paint and no empty message
    // shows before the first load ends.
    if (owner.loading && total === 0) {
      return (
        <li>
          <LoadingState shape="list" rows={6} />
        </li>
      );
    }
    const errorBlock =
      owner.loadError === null ? null : <li key="load-error">{loadErrorBlock(list)}</li>;
    // A failed load with nothing loaded shows only the error, never the empty state; with rows
    // the error sits above them.
    if (total === 0) {
      if (errorBlock !== null) return errorBlock;
      const empty = emptyStates[filter];
      return (
        <li>
          <EmptyState icon={empty.icon} title={empty.title} subtitle={empty.subtitle} />
        </li>
      );
    }
    if (rows.length === 0) {
      return (
        <>
          {errorBlock}
          <li>
            <EmptyState
              icon={SearchX}
              title={t('user.panes.list.noMatch')}
              subtitle={t('user.panes.list.noMatchHint')}
            />
          </li>
        </>
      );
    }
    return (
      <>
        {errorBlock}
        {rows}
      </>
    );
  };

  // ============================================================
  // Detail pane
  // ============================================================

  const accountActions: AccountActions = {
    onEdit: (account: UserAccount) =>
      setEditor({ account, username: account.username, password: '' }),
    onSetDisabled: accountsState.setAccountDisabled,
    onDelete: (account: UserAccount) => setConfirmation({ account }),
    disablingAccountId: accountsState.disablingAccountId
  };

  const findSession = (id: string): Session | undefined =>
    sessions.find((session: Session) => session.id === id) ??
    historySessions.find((session: Session) => session.id === id);

  // The session the pane shows: a session pick, or an account opened on one of its sessions.
  const shownSessionId =
    shownPick?.kind === 'session'
      ? shownPick.id
      : shownPick?.kind === 'account'
        ? shownPick.sessionId
        : null;
  const shownSession = shownSessionId === null ? undefined : findSession(shownSessionId);
  const guestThreadDefaults = useGuestThreadDefaults(
    shownSession !== undefined && isGuestSession(shownSession) ? shownSession.id : null
  );
  // Keyed by what it shows, so the pane's own scroll opens each new pick at its top.
  const paneKey =
    shownSession !== undefined
      ? `session:${shownSession.id}`
      : shownPick?.kind === 'account'
        ? `account:${shownPick.id}`
        : (shownPick?.kind ?? 'none');

  // On a phone the page itself scrolls: a tapped pick opens at the page's top, where the pane's
  // name and Back sit below the pinned app nav, and going back to the list puts its scroll back.
  useLayoutEffect(() => {
    if (isDesktop) return;
    if (phoneView === 'detail') {
      if (!openAtTopRef.current) return;
      openAtTopRef.current = false;
      globalThis.scrollTo(0, 0);
      return;
    }
    if (listScrollRef.current === null) return;
    globalThis.scrollTo(0, listScrollRef.current);
    listScrollRef.current = null;
  }, [isDesktop, phoneView, paneKey]);

  const sessionPane = (session: Session): React.ReactNode => {
    const ownerId =
      typeof session.accountId === 'string' && session.accountId !== '' ? session.accountId : null;
    return (
      <SessionDetail
        // Keyed by the session it shows, so a session that ends keeps its pane.
        key={session.id}
        session={session}
        account={
          ownerId === null
            ? null
            : (accounts.find((account: UserAccount) => account.id === ownerId) ?? null)
        }
        otherSessions={
          ownerId === null
            ? []
            : activeSessionsOf(ownerId).filter((other: Session) => other.id !== session.id)
        }
        availableThemes={availableThemes}
        defaultGuestTheme={defaultGuestTheme}
        defaultGuestRefreshRate={defaultGuestRefreshRate}
        guestRefreshRateLocked={guestRefreshRateLocked}
        guestDefaultsLoaded={guestDefaultsLoaded}
        guestDefaultsError={guestDefaultsError}
        onRetryGuestDefaults={() => loadGuestDefaults()}
        guestThreadDefaults={guestThreadDefaults}
        accountActions={accountActions}
        getSessionStatus={getSessionStatus}
        onPickSession={(sessionId: string) => choose({ kind: 'session', id: sessionId })}
        onRevoke={setRevoking}
        onDelete={setDeleting}
      />
    );
  };

  // The pick's target is not in the lists yet: its loader is still out, or failed first.
  const waitingPane = (list: 'sessions' | 'accounts'): React.ReactNode => {
    const owner = list === 'accounts' ? accountsState : sessionsState;
    return (
      <div className="users-pane-body">
        {owner.loadError !== null && !owner.loading ? (
          loadErrorBlock(list)
        ) : (
          <LoadingState shape="fields" />
        )}
      </div>
    );
  };

  const guestAccessPane = (): React.ReactNode => (
    <>
      {guestDefaultsError !== null && (
        <div className="users-pane-body">
          <ErrorBlock
            title={t('user.guest.errors.loadDefaults')}
            message={guestDefaultsError}
            retryLabel={t('common.retry')}
            onRetry={() => void loadGuestDefaults()}
            retrying={loadingGuestDefaults}
          />
        </div>
      )}
      {guestDefaultsLoaded || guestDefaultsError !== null ? (
        <GuestConfiguration
          key="guestAccess"
          guestDuration={guestDuration}
          onDurationChange={handleUpdateDuration}
          guestDefaultsError={guestDefaultsError}
          guestModeLocked={guestModeLocked}
          onToggleGuestLock={handleToggleGuestLock}
          guestCount={sessionsState.loaded ? guestSessions.length : null}
          onGuestsChanged={() => void sessionsState.reload()}
          defaultGuestTheme={defaultGuestTheme}
          onGuestThemeChange={handleUpdateGuestTheme}
          defaultGuestRefreshRate={defaultGuestRefreshRate}
          onGuestRefreshRateChange={handleUpdateGuestRefreshRate}
          guestRefreshRateLocked={guestRefreshRateLocked}
          onGuestRefreshRateLockChange={handleUpdateGuestRefreshRateLock}
          availableThemes={availableThemes}
        />
      ) : (
        <div className="users-pane-body">
          <LoadingState shape="fields" />
        </div>
      )}
    </>
  );

  const detailContent = (): React.ReactNode => {
    if (shownPick === null) return waitingPane('sessions');
    switch (shownPick.kind) {
      case 'guestAccess':
        // A user-role account lands here only when its own session is not in the list, which is
        // the sessions load still out or failed.
        return guestAccessVisible ? guestAccessPane() : waitingPane('sessions');
      case 'signIn':
        // A viewer who may not see the entry is moved to the default by the pick check.
        return signInVisible ? <SignInPane key="signIn" /> : null;
      case 'session': {
        const session = findSession(shownPick.id);
        return session === undefined ? waitingPane('sessions') : sessionPane(session);
      }
      case 'account': {
        const session = shownPick.sessionId === null ? undefined : findSession(shownPick.sessionId);
        if (session !== undefined) return sessionPane(session);
        const account = accounts.find((candidate: UserAccount) => candidate.id === shownPick.id);
        if (account !== undefined && shownPick.sessionId === null) {
          return (
            <AccountDetail key={account.id} account={account} accountActions={accountActions} />
          );
        }
        return waitingPane(account === undefined ? 'accounts' : 'sessions');
      }
    }
  };

  return (
    // App's <main> already pads the page; a second padded container narrowed the card so the four
    // filters no longer fit a 390px phone.
    <div className="animate-fadeIn">
      <Card padding="none" className="users-card">
        <div className="users-split" data-phone-view={phoneView}>
          <section className="users-list-pane" aria-label={t('user.panes.list.aria')}>
            <div className="users-list-inner">
              <div className="users-tools">
                <SearchInput
                  value={query}
                  onChange={(event: React.ChangeEvent<HTMLInputElement>) =>
                    setQuery(event.target.value)
                  }
                  onClear={() => setQuery('')}
                  placeholder={t('activeSessions.searchPlaceholder')}
                  aria-label={t('activeSessions.searchPlaceholder')}
                />
                <SegmentedControl
                  showLabels
                  fullWidth
                  size="md"
                  value={filter}
                  onChange={(value: string) => setFilter(value as ListFilter)}
                  options={LIST_FILTERS.map((key: ListFilter) => ({
                    value: key,
                    label: (
                      <>
                        {filterLabels[key]}
                        <Badge variant="neutral" className="badge-count">
                          {counts[key]}
                        </Badge>
                      </>
                    )
                  }))}
                />
              </div>
              <CustomScrollbar
                className="users-list-scroll"
                maxHeight="100%"
                paddingMode="none"
                radius="none"
              >
                {(guestAccessVisible || signInVisible) && (
                  <ul className="users-rows users-rows--settings">
                    {guestAccessVisible && (
                      <UserListRow
                        quiet
                        lead={<DoorOpen className="users-row__icon" aria-hidden="true" />}
                        title={t('activeSessions.access.title')}
                        line={
                          guestDuration === null
                            ? null
                            : guestModeLocked
                              ? t('user.panes.list.guestAccessLocked', {
                                  count: guestDuration.durationHours
                                })
                              : t('user.panes.list.guestAccessOpen', {
                                  count: guestDuration.durationHours
                                })
                        }
                        selected={shownPick?.kind === 'guestAccess'}
                        onSelect={() => choose({ kind: 'guestAccess' })}
                      />
                    )}
                    {signInVisible && (
                      <UserListRow
                        quiet
                        lead={<KeyRound className="users-row__icon" aria-hidden="true" />}
                        title={t('user.panes.list.signIn')}
                        line={t(`accessSetup.modes.${accountMode}.title`)}
                        selected={shownPick?.kind === 'signIn'}
                        onSelect={() => choose({ kind: 'signIn' })}
                      />
                    )}
                  </ul>
                )}
                <ul className="users-rows users-rows--people" aria-label={filterLabels[filter]}>
                  {listContent()}
                </ul>
              </CustomScrollbar>
              {/* With sign-in off no account exists, and the server refuses to create one; it also
                  refuses a user-role account. */}
              {authenticationEnabled && sessionType !== 'user' && (
                <div className="users-list-foot">
                  <Button
                    variant="filled"
                    color="primary"
                    size="md"
                    fullWidth
                    onClick={() => setEditor({ account: null, username: '', password: '' })}
                  >
                    {t('user.accounts.actions.create')}
                  </Button>
                </div>
              )}
            </div>
          </section>
          <div className="users-detail-pane">
            {!isDesktop && (
              <div className="users-back">
                <Button
                  variant="default"
                  size="sm"
                  className="pointer-target-44"
                  leftSection={<ChevronLeft className="w-4 h-4" aria-hidden="true" />}
                  onClick={() => setPhoneView('list')}
                >
                  {t('user.panes.list.back')}
                </Button>
              </div>
            )}
            <CustomScrollbar
              key={paneKey}
              className="users-detail-scroll"
              maxHeight="100%"
              paddingMode="none"
              radius="none"
            >
              {detailContent()}
            </CustomScrollbar>
          </div>
        </div>
      </Card>

      <SessionDialogs
        revoking={revoking}
        onRevokeClose={() => setRevoking(null)}
        deleting={deleting}
        onDeleteClose={() => setDeleting(null)}
        onSessionsChange={() => void sessionsState.reload()}
      />
      <AccountDialogs
        editor={editor}
        onEditorChange={setEditor}
        onEditorClose={() => setEditor(null)}
        confirmation={confirmation}
        onConfirmationClose={() => setConfirmation(null)}
        saveAccount={accountsState.saveAccount}
        deleteAccount={accountsState.deleteAccount}
      />
    </div>
  );
};

export default UserTab;

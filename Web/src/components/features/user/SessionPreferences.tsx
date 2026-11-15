import React, { useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Alert } from '@components/ui/Alert';
import { Button } from '@components/ui/Button';
import { ErrorBlock } from '@components/ui/ErrorBlock';
import { EnhancedDropdown } from '@components/ui/EnhancedDropdown';
import { MultiSelectDropdown } from '@components/ui/MultiSelectDropdown';
import { ToggleSwitch } from '@components/ui/ToggleSwitch';
import { LoadingState } from '@components/ui/ManagerCard';
import ApiService from '@services/api.service';
import themeService from '@services/theme.service';
import { isAccountHolder } from '@services/auth.service';
import { useSignalR } from '@contexts/SignalRContext/useSignalR';
import { useAuth } from '@contexts/useAuth';
import { useRefreshRate } from '@contexts/useRefreshRate';
import type {
  GuestRefreshRateUpdatedEvent,
  UserPreferencesUpdatedEvent
} from '@contexts/SignalRContext/types';
import { useErrorHandler } from '@hooks/useErrorHandler';
import { useReconnectRefetch } from '@hooks/useReconnectRefetch';
import { useDefaultGuestPreferences } from '@hooks/useDefaultGuestPreferences';
import { getErrorMessage } from '@utils/error';
import {
  PREFILL_SERVICES,
  type PrefillServiceConfig
} from '@components/features/prefill/hooks/prefillServiceConfig';
import { DEFAULT_PREFERENCES, type UserPreferences } from '@/types/userPreferences';
import type { GameServiceId } from '@/types/gameService';
import { type Session, type ThemeOption, refreshRateOptions } from './types';
import type { GuestThreadDefaults } from './useGuestThreadDefaults';
import { withoutSent } from './draftEdits';
import { getOnOffOptions } from './onOffOptions';
import { getThreadOptions } from './threadOptions';
import { getTimeFormatOptions, guestTimeFormatKeys } from './timeFormatOptions';
import UserPaneSection from './UserPaneSection';
import UserSettingRow from './UserSettingRow';

interface SessionPreferencesProps {
  session: Session;
  availableThemes: ThemeOption[];
  defaultGuestTheme: string;
  defaultGuestRefreshRate: string;
  guestRefreshRateLocked: boolean;
  guestDefaultsLoaded: boolean;
  guestThreadDefaults: GuestThreadDefaults;
}

/** A service whose rows carry a per-session thread limit. */
type ThreadedService = Extract<PrefillServiceConfig, { supportsMaxThreads: true }>;

// The prefill access section and the preference rows each save with their own Save button. The values
// are read from the server here rather than from the shared preferences cache, which drops the thread
// overrides and skips guest resets for an admin.
const SessionPreferences: React.FC<SessionPreferencesProps> = ({
  session,
  availableThemes,
  defaultGuestTheme,
  defaultGuestRefreshRate,
  guestRefreshRateLocked,
  guestDefaultsLoaded,
  guestThreadDefaults
}) => {
  const { t } = useTranslation();
  const { notifyError } = useErrorHandler();
  const { on, off, isConnected } = useSignalR();
  const { prefs: defaultGuestPrefs } = useDefaultGuestPreferences();
  const { sessionType: viewerSessionType } = useAuth();
  const { systemRate } = useRefreshRate();
  const guest = session.sessionType === 'guest';
  // A guest and a user-role account follow a rate of their own, which an admin may lock; an admin
  // session follows the install-wide rate.
  const hasOwnRate = guest || session.sessionType === 'user';
  // A user-role account reaches only its own account's sessions, and the server keeps the admin-only
  // columns (the lock, the allowed formats) from its writes.
  const viewerIsUser = viewerSessionType === 'user';

  const [prefs, setPrefs] = useState<UserPreferences>(DEFAULT_PREFERENCES);
  const [prefsLoaded, setPrefsLoaded] = useState(false);
  const [loadError, setLoadError] = useState<string | null>(null);
  // The pane saves together with its one Save button: each preference row the admin changed waits
  // here until Save sends it. A Use default is a null entry.
  const [draftPrefs, setDraftPrefs] = useState<Partial<UserPreferences>>({});
  const [saving, setSaving] = useState(false);
  // The first failure of the newest Save, shown above Save until the next Save ends.
  const [saveFailure, setSaveFailure] = useState<unknown>(null);
  // A Retry's read can be held behind a save or replaced by a pushed reload, so the Retry stays
  // busy until the newest read settles.
  const [prefsRetrying, setPrefsRetrying] = useState(false);
  // Each prefill switch and thread limit the admin changed waits here, per service, until Save
  // sends it.
  const [draftAccess, setDraftAccess] = useState<Partial<Record<GameServiceId, boolean>>>({});
  const [draftThreads, setDraftThreads] = useState<Partial<Record<GameServiceId, number | null>>>(
    {}
  );
  // The access a Save sent, per service, drawn until the session shows it: the call can answer
  // before the permission push redraws the session, and the switch must not fall back meanwhile.
  const [requestedAccess, setRequestedAccess] = useState<Partial<Record<GameServiceId, boolean>>>(
    {}
  );

  // Each write reads the whole preferences object and PUTs it back, so two overlapping writes
  // would let the later PUT restore the field the earlier one changed: writes run one at a time.
  const writeChainRef = useRef<Promise<void>>(Promise.resolve());
  // Last value the server confirmed per row (set on load, on a push reload and on each successful
  // write). A failed write restores it only when no later write for that row is waiting, so a
  // queued value never gets undone by an earlier failure.
  const confirmedRef = useRef<UserPreferences>(DEFAULT_PREFERENCES);
  const pendingRef = useRef<Map<keyof UserPreferences, number>>(new Map());
  // Writes queued or running across all rows; a push-driven reload waits while it is above 0.
  const pendingTotalRef = useRef(0);
  // Counts clicks. A reload whose GET was read before a click must not write its older value over
  // the clicked row, so the reload compares this before and after its GET.
  const clickCountRef = useRef(0);
  const reloadRequestRef = useRef(0);
  const reloadOwedRef = useRef(false);

  const reloadPrefs = async (): Promise<void> => {
    if (pendingTotalRef.current > 0) {
      reloadOwedRef.current = true;
      return;
    }
    const request = ++reloadRequestRef.current;
    const clicksBefore = clickCountRef.current;
    try {
      const loaded = await ApiService.getSessionPreferences<UserPreferences>(session.id);
      const fresh: UserPreferences = {
        selectedTheme:
          typeof loaded.selectedTheme === 'string' && loaded.selectedTheme.trim() !== ''
            ? loaded.selectedTheme
            : null,
        sharpCorners: loaded.sharpCorners,
        disableFocusOutlines: loaded.disableFocusOutlines,
        disableTooltips: loaded.disableTooltips,
        picsAlwaysVisible: loaded.picsAlwaysVisible,
        disableStickyNotifications: loaded.disableStickyNotifications,
        showDatasourceLabels: loaded.showDatasourceLabels,
        useLocalTimezone: loaded.useLocalTimezone,
        useUtcTimezone: loaded.useUtcTimezone,
        use24HourFormat: loaded.use24HourFormat,
        refreshRate: loaded.refreshRate ?? null,
        refreshRateLocked: loaded.refreshRateLocked ?? null,
        allowedTimeFormats: loaded.allowedTimeFormats ?? null,
        steamMaxThreadCount: loaded.steamMaxThreadCount ?? null,
        epicMaxThreadCount: loaded.epicMaxThreadCount ?? null,
        xboxMaxThreadCount: loaded.xboxMaxThreadCount ?? null,
        battleNetMaxThreadCount: loaded.battleNetMaxThreadCount ?? null,
        riotMaxThreadCount: loaded.riotMaxThreadCount ?? null
      };
      if (request !== reloadRequestRef.current) return;
      if (clickCountRef.current !== clicksBefore) {
        // Read before a click: drop it. The click's own write ends by reloading (finally below);
        // when that write already ended, read again now.
        if (pendingTotalRef.current > 0) reloadOwedRef.current = true;
        else void reloadPrefs();
        return;
      }
      setPrefs(fresh);
      confirmedRef.current = fresh;
      setPrefsLoaded(true);
      setLoadError(null);
      setPrefsRetrying(false);
    } catch (err: unknown) {
      if (request !== reloadRequestRef.current) return;
      // Rows already on screen stay; the error and its Retry show above them.
      setLoadError(getErrorMessage(err));
      setPrefsRetrying(false);
    }
  };

  const retryPrefs = (): Promise<void> => {
    setPrefsRetrying(true);
    return reloadPrefs();
  };

  // Rejects with the error when the write fails, after putting the row back; the caller tells the
  // admin. The chain itself never rejects, so a failed write does not stop the ones queued after it.
  const writePreference = <K extends keyof UserPreferences>(
    key: K,
    value: UserPreferences[K]
  ): Promise<void> => {
    clickCountRef.current += 1;
    setPrefs((current: UserPreferences) => ({ ...current, [key]: value }));
    pendingRef.current.set(key, (pendingRef.current.get(key) ?? 0) + 1);
    pendingTotalRef.current += 1;
    const write = writeChainRef.current.then(async (): Promise<void> => {
      try {
        // The viewer's own display switches go through the app's own store, which also repaints.
        let ownStore: Promise<boolean> | null = null;
        if (session.isCurrentSession) {
          const flag = value === true;
          if (key === 'sharpCorners') ownStore = themeService.setSharpCorners(flag);
          else if (key === 'disableTooltips') ownStore = themeService.setDisableTooltips(flag);
          else if (key === 'picsAlwaysVisible') ownStore = themeService.setPicsAlwaysVisible(flag);
          else if (key === 'disableStickyNotifications')
            ownStore = themeService.setDisableStickyNotifications(flag);
        }
        if (key === 'refreshRate') {
          // This endpoint also tells the guest's browser about the new rate.
          await ApiService.setSessionRefreshRate(
            session.id,
            typeof value === 'string' ? value : ''
          );
        } else if (ownStore !== null) {
          if (!(await ownStore)) {
            throw new Error(t('management.sections.displayPreferences.saveFailed'));
          }
        } else {
          // Read the stored object first so the PUT never writes back a value the guest changed
          // meanwhile or a field the broadcast left out.
          const fresh = await ApiService.getSessionPreferences<UserPreferences>(session.id);
          await ApiService.saveSessionPreferences<void>(session.id, { ...fresh, [key]: value });
        }
        confirmedRef.current = { ...confirmedRef.current, [key]: value };
        // Put the saved value back on screen unless a later change to this row is queued, so a
        // reload answer that landed meanwhile cannot leave the row on its older value.
        if (pendingRef.current.get(key) === 1) {
          setPrefs((current: UserPreferences) => ({ ...current, [key]: value }));
        }
        // Use default (null) is repainted by the server's preferences broadcast, which loads the
        // default theme. The theme is stored by now, so a failed theme download is a paint failure,
        // not a failed save.
        if (
          session.isCurrentSession &&
          key === 'selectedTheme' &&
          typeof value === 'string' &&
          value !== ''
        ) {
          try {
            await themeService.setTheme(value);
          } catch (err: unknown) {
            notifyError(t('management.themes.notifications.preferenceChangeFailed'), err, {
              logLabel: 'Failed to apply saved theme'
            });
          }
        }
      } catch (err: unknown) {
        if (pendingRef.current.get(key) === 1) {
          setPrefs((current: UserPreferences) => ({
            ...current,
            [key]: confirmedRef.current[key]
          }));
        }
        throw err;
      } finally {
        pendingRef.current.set(key, (pendingRef.current.get(key) ?? 1) - 1);
        pendingTotalRef.current -= 1;
        if (pendingTotalRef.current === 0 && reloadOwedRef.current) {
          reloadOwedRef.current = false;
          void reloadPrefs();
        }
      }
    });
    writeChainRef.current = write.catch((): void => undefined);
    return write;
  };

  // Whether a preference row is drawn now. Save counts and sends only the edits of drawn rows: a row
  // can disappear while its edit waits, as when an admin locks the rate a user-role account was changing.
  const rowDrawn = (key: keyof UserPreferences): boolean => {
    switch (key) {
      case 'selectedTheme':
        return guestDefaultsLoaded;
      case 'refreshRate':
        return (
          hasOwnRate && guestDefaultsLoaded && (!viewerIsUser || prefs.refreshRateLocked !== true)
        );
      case 'refreshRateLocked':
        // A guest's unset lock follows the lock for every guest, which is unknown until it loads.
        return hasOwnRate && !viewerIsUser && (!guest || guestDefaultsLoaded);
      case 'disableStickyNotifications':
      case 'picsAlwaysVisible':
        return isAccountHolder(session.sessionType ?? null);
      case 'allowedTimeFormats':
        return !viewerIsUser;
      default:
        return true;
    }
  };

  // A change back to the stored value is no change, so it leaves the draft. Formats are compared as
  // sets, the way the Use default check below compares them.
  const editPreference = <K extends keyof UserPreferences>(
    key: K,
    value: UserPreferences[K]
  ): void => {
    const stored = prefs[key];
    const same =
      Array.isArray(value) && Array.isArray(stored)
        ? value.length === stored.length && value.every((format: string) => stored.includes(format))
        : value === stored;
    setDraftPrefs((previous: Partial<UserPreferences>) => {
      const rest = { ...previous };
      if (same) delete rest[key];
      else rest[key] = value;
      return rest;
    });
  };

  // Sends the edit of every drawn preference row, each through the write chain, and drops the edit of
  // a row no longer drawn. A row edited again while Save runs stays a change; a row whose save failed
  // keeps its edit.
  const savePreferences = (): Promise<void>[] => {
    const sent = draftPrefs;
    const keys = Object.keys(sent) as (keyof UserPreferences)[];
    const hidden = keys.filter((key: keyof UserPreferences) => !rowDrawn(key));
    if (hidden.length > 0) {
      setDraftPrefs((previous: Partial<UserPreferences>) => {
        const rest = { ...previous };
        for (const key of hidden) delete rest[key];
        return rest;
      });
    }
    return keys.filter(rowDrawn).map(async (key: keyof UserPreferences): Promise<void> => {
      const value = sent[key];
      await writePreference(key, value);
      setDraftPrefs((previous: Partial<UserPreferences>) => withoutSent(previous, key, value));
    });
  };

  // The pane is keyed by its session, so reloadPrefs only ever reads this session; the handlers are
  // bound once per mount.
  useEffect(() => {
    void reloadPrefs();

    const handlePreferencesUpdated = (event: UserPreferencesUpdatedEvent): void => {
      // An account holder's write of another session is broadcast with the thread caps left out,
      // so the pushed values are never applied: the pane reads the stored row again.
      if (event.sessionId === session.id) void reloadPrefs();
    };
    const handleRefreshRateUpdated = (event: GuestRefreshRateUpdatedEvent): void => {
      if (event.sessionId === session.id) void reloadPrefs();
    };
    const handlePreferencesReset = (): void => {
      void reloadPrefs();
    };

    on('UserPreferencesUpdated', handlePreferencesUpdated);
    on('GuestRefreshRateUpdated', handleRefreshRateUpdated);
    on('UserPreferencesReset', handlePreferencesReset);

    return () => {
      off('UserPreferencesUpdated', handlePreferencesUpdated);
      off('GuestRefreshRateUpdated', handleRefreshRateUpdated);
      off('UserPreferencesReset', handlePreferencesReset);
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [on, off]);

  // A push missed while the socket was down is recovered by reading again.
  useReconnectRefetch(isConnected, () => {
    void reloadPrefs();
  });

  // A requested access is let go once the pushed session shows it, so a later change made
  // elsewhere is drawn as it arrives.
  useEffect(() => {
    setRequestedAccess((previous: Partial<Record<GameServiceId, boolean>>) => {
      const shown = PREFILL_SERVICES.filter(
        (service: PrefillServiceConfig) =>
          previous[service.id] === session[service.sessionEnabledField]
      );
      if (shown.length === 0) return previous;
      const rest = { ...previous };
      for (const service of shown) delete rest[service.id];
      return rest;
    });
  }, [session]);

  // A change back to the value the row shows without it is no change, so it leaves the draft.
  const editAccess = (service: PrefillServiceConfig, enabled: boolean): void => {
    const shown = requestedAccess[service.id] ?? session[service.sessionEnabledField];
    setDraftAccess((previous: Partial<Record<GameServiceId, boolean>>) => {
      const rest = { ...previous };
      if (enabled === shown) delete rest[service.id];
      else rest[service.id] = enabled;
      return rest;
    });
  };

  const editThreads = (service: ThreadedService, threadCount: number | null): void => {
    const stored = prefs[service.preferenceThreadField] ?? null;
    setDraftThreads((previous: Partial<Record<GameServiceId, number | null>>) => {
      const rest = { ...previous };
      if (threadCount === stored) delete rest[service.id];
      else rest[service.id] = threadCount;
      return rest;
    });
  };

  // Sends every prefill row that changed. Thread limits go through the preferences write chain;
  // grants and revokes write the session row, so they run beside it. Their reply carries nothing; the
  // permission push updates the session shown, and it can arrive before the reply, so the sent access
  // is held from the moment it is sent. A row edited again while Save runs stays a change; a row
  // whose save failed keeps its edit.
  const savePrefillAccess = (): Promise<void>[] => {
    const sentAccess = draftAccess;
    const sentThreads = draftThreads;
    const threadWrites = PREFILL_SERVICES.filter(
      (service: PrefillServiceConfig): service is ThreadedService =>
        service.supportsMaxThreads && sentThreads[service.id] !== undefined
    ).map(async (service: ThreadedService): Promise<void> => {
      const threadCount = sentThreads[service.id] ?? null;
      await writePreference(service.preferenceThreadField, threadCount);
      setDraftThreads((previous: Partial<Record<GameServiceId, number | null>>) =>
        withoutSent(previous, service.id, threadCount)
      );
    });
    const accessWrites = PREFILL_SERVICES.filter(
      (service: PrefillServiceConfig) => sentAccess[service.id] !== undefined
    ).map(async (service: PrefillServiceConfig): Promise<void> => {
      const enabled = sentAccess[service.id] === true;
      setRequestedAccess((previous: Partial<Record<GameServiceId, boolean>>) => ({
        ...previous,
        [service.id]: enabled
      }));
      try {
        await ApiService.toggleGuestPrefillService(session.id, service.id, enabled);
      } catch (err: unknown) {
        setRequestedAccess((previous: Partial<Record<GameServiceId, boolean>>) => {
          const rest = { ...previous };
          delete rest[service.id];
          return rest;
        });
        throw err;
      }
      setDraftAccess((previous: Partial<Record<GameServiceId, boolean>>) =>
        withoutSent(previous, service.id, enabled)
      );
    });
    return [...threadWrites, ...accessWrites];
  };

  // The pane's one Save sends the prefill rows and the preference rows together.
  const saveAll = async (): Promise<void> => {
    if (saving) return;
    setSaving(true);
    const results = await Promise.allSettled([...savePrefillAccess(), ...savePreferences()]);
    setSaving(false);
    const failed = results.find(
      (result: PromiseSettledResult<void>): result is PromiseRejectedResult =>
        result.status === 'rejected'
    );
    setSaveFailure(failed === undefined ? null : failed.reason);
  };

  if (!prefsLoaded) {
    return loadError !== null ? (
      <ErrorBlock
        title={t('user.panes.prefs.loadFailed')}
        message={loadError}
        retryLabel={t('common.retry')}
        onRetry={retryPrefs}
        retrying={prefsRetrying}
      />
    ) : (
      <LoadingState shape="fields" rows={4} />
    );
  }

  const onOffOptions = getOnOffOptions(t);
  const threadOptions = getThreadOptions(t);
  const timeFormatOptions = getTimeFormatOptions(t, guestTimeFormatKeys);
  const translatedRefreshRateOptions = refreshRateOptions.map(
    (option: { value: string; label: string }) => ({
      ...option,
      label: t(`user.guest.refreshRates.${option.value}`)
    })
  );
  // A row draws its edit waiting for Save, else the stored value. A Use default waiting for Save is a
  // null entry, which must not fall through to the stored value.
  const drawnPref = <K extends keyof UserPreferences>(key: K): UserPreferences[K] => {
    const drafted = draftPrefs[key];
    return drafted !== undefined ? drafted : prefs[key];
  };
  const drawnTheme = drawnPref('selectedTheme');
  const drawnRate = drawnPref('refreshRate');
  const drawnLocked = drawnPref('refreshRateLocked');
  const drawnFormats = drawnPref('allowedTimeFormats');
  // A user-role account without a rate of its own follows the install-wide rate.
  const defaultRate = guest ? defaultGuestRefreshRate : systemRate;
  const defaultFormats: string[] = defaultGuestPrefs.allowedTimeFormats;
  const formatsOverridden =
    drawnFormats != null &&
    !(
      drawnFormats.length === defaultFormats.length &&
      drawnFormats.every((format: string) => defaultFormats.includes(format))
    );
  const hasChanges =
    Object.keys(draftAccess).length > 0 ||
    Object.keys(draftThreads).length > 0 ||
    (Object.keys(draftPrefs) as (keyof UserPreferences)[]).some(rowDrawn);

  return (
    <>
      {loadError !== null && (
        <ErrorBlock
          title={t('user.panes.prefs.loadFailed')}
          message={loadError}
          retryLabel={t('common.retry')}
          onRetry={retryPrefs}
          retrying={prefsRetrying}
        />
      )}

      {guest && (
        <UserPaneSection title={t('activeSessions.prefill.title')}>
          {guestThreadDefaults.error !== null && (
            <ErrorBlock
              title={t('user.errors.loadThreadConfig')}
              message={guestThreadDefaults.error}
              retryLabel={t('common.retry')}
              onRetry={guestThreadDefaults.retry}
              retrying={guestThreadDefaults.retrying}
            />
          )}
          <div className="users-svc-head" aria-hidden="true">
            <span>{t('user.panes.prefs.threads')}</span>
          </div>
          {PREFILL_SERVICES.map((service: PrefillServiceConfig) => {
            const ServiceIcon = service.icon;
            // A Use default waiting for Save is a null draft, which must not fall through to the
            // stored override.
            const draftThreadCount = draftThreads[service.id];
            const override = service.supportsMaxThreads
              ? draftThreadCount !== undefined
                ? draftThreadCount
                : (prefs[service.preferenceThreadField] ?? null)
              : null;
            const threadDefault = guestThreadDefaults.values[service.id];
            return (
              <div key={service.id} className="users-svc users-svc--one">
                <ServiceIcon size={20} className="users-svc__icon" />
                <span className="users-svc__name">{service.displayName}</span>
                <ToggleSwitch
                  size="md"
                  options={onOffOptions}
                  value={
                    (draftAccess[service.id] ??
                    requestedAccess[service.id] ??
                    session[service.sessionEnabledField])
                      ? 'on'
                      : 'off'
                  }
                  onChange={(value: string): void => editAccess(service, value === 'on')}
                />
                {service.supportsMaxThreads && (
                  <div className="users-svc__cells">
                    <div className="users-svc__cell">
                      <span className="users-svc__cell-label">{t('user.panes.prefs.threads')}</span>
                      <span className="user-settings-dropdown">
                        <EnhancedDropdown
                          variant="button"
                          size="md"
                          options={threadOptions}
                          value={
                            override !== null
                              ? String(override)
                              : threadDefault !== null
                                ? String(threadDefault)
                                : ''
                          }
                          onChange={(value: string): void =>
                            editThreads(service, value === '' ? null : Number(value))
                          }
                          triggerAriaLabel={t('user.panes.prefs.threadsFor', {
                            service: service.displayName
                          })}
                          className="w-full"
                        />
                      </span>
                      {override !== null && (
                        <Button
                          variant="default"
                          size="md"
                          onClick={() => editThreads(service, null)}
                        >
                          {t('actions.useDefault')}
                        </Button>
                      )}
                    </div>
                  </div>
                )}
              </div>
            );
          })}
        </UserPaneSection>
      )}

      <UserPaneSection title={t('user.panes.sections.prefs')}>
        {rowDrawn('selectedTheme') && (
          <UserSettingRow label={t('activeSessions.labels.theme')}>
            {drawnTheme !== null && drawnTheme !== defaultGuestTheme && (
              <Button
                variant="default"
                size="md"
                onClick={() => editPreference('selectedTheme', null)}
              >
                {t('actions.useDefault')}
              </Button>
            )}
            <span className="user-settings-dropdown">
              <EnhancedDropdown
                variant="button"
                size="md"
                options={availableThemes.map((theme: ThemeOption) => ({
                  value: theme.id,
                  label: theme.name
                }))}
                value={drawnTheme ?? defaultGuestTheme}
                onChange={(value: string): void => editPreference('selectedTheme', value)}
                triggerAriaLabel={t('activeSessions.labels.theme')}
                className="w-full"
              />
            </span>
          </UserSettingRow>
        )}

        {rowDrawn('refreshRate') && (
          <UserSettingRow label={t('user.panes.prefs.refreshRate')}>
            {drawnRate != null && drawnRate !== defaultRate && (
              <Button
                variant="default"
                size="md"
                onClick={() => editPreference('refreshRate', null)}
              >
                {t('actions.useDefault')}
              </Button>
            )}
            <span className="user-settings-dropdown">
              <EnhancedDropdown
                variant="button"
                size="md"
                options={translatedRefreshRateOptions}
                value={drawnRate ?? defaultRate}
                onChange={(value: string): void => editPreference('refreshRate', value)}
                triggerAriaLabel={t('user.panes.prefs.refreshRate')}
                className="w-full"
              />
            </span>
          </UserSettingRow>
        )}

        {rowDrawn('refreshRateLocked') && (
          <UserSettingRow
            label={
              guest
                ? t('user.panes.prefs.mayChangeRate')
                : t('user.panes.prefs.accountMayChangeRate')
            }
          >
            {drawnLocked != null && (
              <Button
                variant="default"
                size="md"
                onClick={() => editPreference('refreshRateLocked', null)}
              >
                {t('actions.useDefault')}
              </Button>
            )}
            {/* Unset, a guest follows the lock for every guest and a user-role account may change its
                rate. */}
            <ToggleSwitch
              size="md"
              options={onOffOptions}
              value={(drawnLocked ?? (guest && guestRefreshRateLocked)) ? 'off' : 'on'}
              onChange={(value: string): void =>
                editPreference('refreshRateLocked', value === 'off')
              }
            />
          </UserSettingRow>
        )}

        <UserSettingRow label={t('user.guest.preferences.sharpCorners.label')}>
          <ToggleSwitch
            size="md"
            options={onOffOptions}
            value={drawnPref('sharpCorners') ? 'on' : 'off'}
            onChange={(value: string): void => editPreference('sharpCorners', value === 'on')}
          />
        </UserSettingRow>

        <UserSettingRow label={t('activeSessions.preferencesModal.tooltips')}>
          <ToggleSwitch
            size="md"
            options={onOffOptions}
            value={drawnPref('disableTooltips') ? 'off' : 'on'}
            onChange={(value: string): void => editPreference('disableTooltips', value === 'off')}
          />
        </UserSettingRow>

        {rowDrawn('disableStickyNotifications') && (
          <>
            <UserSettingRow label={t('activeSessions.preferencesModal.stickyNotifications.title')}>
              <ToggleSwitch
                size="md"
                options={onOffOptions}
                value={drawnPref('disableStickyNotifications') ? 'off' : 'on'}
                onChange={(value: string): void =>
                  editPreference('disableStickyNotifications', value === 'off')
                }
              />
            </UserSettingRow>

            <UserSettingRow label={t('user.panes.prefs.keepVisible')}>
              <ToggleSwitch
                size="md"
                options={onOffOptions}
                value={drawnPref('picsAlwaysVisible') ? 'on' : 'off'}
                onChange={(value: string): void =>
                  editPreference('picsAlwaysVisible', value === 'on')
                }
              />
            </UserSettingRow>
          </>
        )}

        <UserSettingRow label={t('user.guest.preferences.datasourceLabels.label')}>
          <ToggleSwitch
            size="md"
            options={onOffOptions}
            value={drawnPref('showDatasourceLabels') ? 'on' : 'off'}
            onChange={(value: string): void =>
              editPreference('showDatasourceLabels', value === 'on')
            }
          />
        </UserSettingRow>

        {rowDrawn('allowedTimeFormats') && (
          <UserSettingRow label={t('user.panes.prefs.timeFormats')}>
            {formatsOverridden && (
              <Button
                variant="default"
                size="md"
                onClick={() => editPreference('allowedTimeFormats', null)}
              >
                {t('actions.useDefault')}
              </Button>
            )}
            <span className="user-settings-dropdown">
              <MultiSelectDropdown
                options={timeFormatOptions}
                values={drawnFormats ?? defaultFormats}
                onChange={(formats: string[]): void =>
                  editPreference('allowedTimeFormats', formats)
                }
                placeholder={t('user.guest.timeFormats.placeholder')}
                minSelections={1}
                dropdownWidth="w-80"
                alignRight
                className="w-full"
              />
            </span>
          </UserSettingRow>
        )}
      </UserPaneSection>

      <div className="users-save-bar">
        {saveFailure !== null && (
          <Alert color="error" title={t('activeSessions.errors.savePreferences')}>
            <p className="text-sm">{getErrorMessage(saveFailure)}</p>
          </Alert>
        )}
        <div className="users-save-bar__row">
          {hasChanges && (
            <span className="text-sm text-themed-secondary">{t('user.panes.unsavedChanges')}</span>
          )}
          <Button
            variant="filled"
            color="primary"
            className="users-pane-btn"
            loading={saving}
            disabled={!hasChanges}
            onClick={() => void saveAll()}
          >
            {t('common.save')}
          </Button>
        </div>
      </div>
    </>
  );
};

export default SessionPreferences;

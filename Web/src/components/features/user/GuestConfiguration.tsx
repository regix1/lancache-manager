import React, { useEffect, useState, useCallback, useRef } from 'react';
import { useTranslation } from 'react-i18next';
import { RotateCcw, Eraser } from 'lucide-react';
import { HelpPopover } from '@components/ui/HelpPopover';
import { RowActionsMenu } from '@components/ui/RowActionsMenu';
import { ActionMenuItem, ActionMenuDivider } from '@components/ui/ActionMenu';
import { ErrorBlock } from '@components/ui/ErrorBlock';
import { Alert } from '@components/ui/Alert';
import { Button } from '@components/ui/Button';
import { EnhancedDropdown } from '@components/ui/EnhancedDropdown';
import { MultiSelectDropdown } from '@components/ui/MultiSelectDropdown';
import { ToggleSwitch } from '@components/ui/ToggleSwitch';
import { Tooltip } from '@components/ui/Tooltip';
import { ConfirmationModal } from '@components/common/ConfirmationModal';
import LoadingSpinner from '@components/common/LoadingSpinner';
import ApiService from '@services/api.service';
import { assertOk } from '@services/apiError';
import { getErrorMessage } from '@utils/error';
import { useErrorHandler } from '@hooks/useErrorHandler';
import { useReconnectRefetch } from '@hooks/useReconnectRefetch';
import type { DefaultGuestPreferences } from '@hooks/useDefaultGuestPreferences';
import { useSignalR } from '@contexts/SignalRContext/useSignalR';
import { useAuth } from '@contexts/useAuth';
import {
  PREFILL_SERVICES,
  prefillServiceRecord,
  type GuestPrefillConfigChangedPayload,
  type PrefillServiceConfig
} from '@components/features/prefill/hooks/prefillServiceConfig';
import type { GameServiceId } from '@/types/gameService';
import type { ClockPreferences } from '@/types/userPreferences';
import type { DefaultGuestPreferencesChangedEvent } from '@contexts/SignalRContext/types';
import { TIME_SETTING_VALUES, type TimeSettingValue } from '@contexts/TimezoneContext.types';
import { clockFromTimeSetting, timeSettingFromClock } from '@utils/pendingPreferences';
import {
  type GuestDurationResponse,
  type ThemeOption,
  durationOptions,
  refreshRateOptions,
  showToast
} from './types';
import UserPaneHeader from './UserPaneHeader';
import UserPaneSection from './UserPaneSection';
import UserSettingRow from './UserSettingRow';
import { getOnOffOptions } from './onOffOptions';
import { getTimeFormatOptions, guestTimeFormatKeys } from './timeFormatOptions';
import {
  toGuestPrefillConfig,
  type GuestPrefillConfig,
  type GuestPrefillConfigResponse
} from './guestPrefillConfig';
import { getThreadOptions } from './threadOptions';
import { withoutSent } from './draftEdits';
import './user-settings.css';

interface DefaultGuestPreferencesResponse {
  useLocalTimezone: boolean;
  useUtcTimezone?: boolean;
  use24HourFormat: boolean;
  sharpCorners: boolean;
  disableTooltips: boolean;
  showDatasourceLabels: boolean;
  allowedTimeFormats?: string[];
}

// The guest settings the host stores, as the pane drafts them. A null length puts it back to the
// configured or built-in one.
interface GuestAccessSettings {
  guestModeLocked: boolean;
  durationHours: number | null;
  theme: string;
  refreshRate: string;
  refreshRateLocked: boolean;
}

type DraftedPreference =
  | 'sharpCorners'
  | 'disableTooltips'
  | 'showDatasourceLabels'
  | 'allowedTimeFormats';

// A setting Save could not store: the title that names it and the reason its request failed.
interface GuestSaveFailure {
  title: string;
  reason: unknown;
}

interface GuestConfigurationProps {
  guestDuration: GuestDurationResponse | null;
  // Each change callback stores one setting and rejects when the server refuses it.
  // null clears the saved length, so the configured or built-in length applies
  onDurationChange: (hours: number | null) => Promise<void>;
  // The host's guest-defaults load error; with guestDuration null it means the first read failed,
  // so the Session length row hides instead of spinning
  guestDefaultsError: string | null;
  guestModeLocked: boolean;
  onToggleGuestLock: (locked: boolean) => Promise<void>;
  // Active guest sessions, or null while the session list has had no load without error; only 0
  // hides "Reset all guests", while "Clear all guests" always shows so a cleanup the server asked
  // to retry can be retried
  guestCount: number | null;
  onGuestsChanged: () => void;
  defaultGuestTheme: string;
  onGuestThemeChange: (themeId: string) => Promise<void>;
  defaultGuestRefreshRate: string;
  onGuestRefreshRateChange: (rate: string) => Promise<void>;
  guestRefreshRateLocked: boolean;
  onGuestRefreshRateLockChange: (locked: boolean) => Promise<void>;
  availableThemes: ThemeOption[];
}

const GuestConfiguration: React.FC<GuestConfigurationProps> = ({
  guestDuration,
  onDurationChange,
  guestDefaultsError,
  guestModeLocked,
  onToggleGuestLock,
  guestCount,
  onGuestsChanged,
  defaultGuestTheme,
  onGuestThemeChange,
  defaultGuestRefreshRate,
  onGuestRefreshRateChange,
  guestRefreshRateLocked,
  onGuestRefreshRateLockChange,
  availableThemes
}) => {
  const { t } = useTranslation();
  const { notifyError } = useErrorHandler();
  const { on, off, isConnected } = useSignalR();
  const { authMode, isAdmin } = useAuth();
  const [defaultGuestPreferences, setDefaultGuestPreferences] = useState<DefaultGuestPreferences>({
    useLocalTimezone: false,
    useUtcTimezone: false,
    use24HourFormat: true,
    sharpCorners: false,
    disableTooltips: false,
    showDatasourceLabels: true,
    allowedTimeFormats: [...TIME_SETTING_VALUES]
  });
  const [loadingDefaultPrefs, setLoadingDefaultPrefs] = useState(false);
  const [defaultPrefsError, setDefaultPrefsError] = useState<string | null>(null);
  const [prefillConfigError, setPrefillConfigError] = useState<string | null>(null);
  const defaultPrefsRequestRef = useRef(0);
  const defaultPrefsLoadedRef = useRef(false);
  // Fields written or pushed since the newest reload started keep their screen value when it answers.
  const defaultPrefsChangedRef = useRef(new Set<keyof DefaultGuestPreferences>());
  // Pushes received per field. The server announces a save before it answers it, so a save's reply
  // applies only to fields no push touched while the save was open.
  const defaultPrefsPushesRef = useRef(new Map<keyof DefaultGuestPreferences, number>());
  const prefillConfigRequestRef = useRef(0);
  // Services saved or pushed since the newest prefill batch started keep their values when it answers.
  const prefillChangedRef = useRef(new Set<GameServiceId>());

  // Guest prefill permissions, one entry per service in PREFILL_SERVICES. The load and
  // update calls below are keyed off the same table, so a new service needs no state here.
  const [prefillConfigs, setPrefillConfigs] = useState<Record<GameServiceId, GuestPrefillConfig>>(
    () =>
      prefillServiceRecord<GuestPrefillConfig>(() => ({
        enabledByDefault: false,
        durationHours: 2,
        maxThreadCount: null
      }))
  );
  const [loadingPrefillConfigs, setLoadingPrefillConfigs] = useState<
    Record<GameServiceId, boolean>
  >(() => prefillServiceRecord<boolean>(() => false));

  // The pane saves together with its Save button: each control the admin changed waits here until
  // Save sends it. The controls draw it on top of the saved values, so a push moves only the
  // controls nobody changed.
  const [draftHost, setDraftHost] = useState<Partial<GuestAccessSettings>>({});
  const [draftPrefs, setDraftPrefs] = useState<
    Partial<Pick<DefaultGuestPreferences, DraftedPreference>>
  >({});
  const [draftPrefill, setDraftPrefill] = useState<
    Partial<Record<GameServiceId, Partial<GuestPrefillConfig>>>
  >({});
  const [saving, setSaving] = useState(false);
  // The first failure of the newest Save, shown above Save until the next Save ends.
  const [saveFailure, setSaveFailure] = useState<GuestSaveFailure | null>(null);

  const [menuOpen, setMenuOpen] = useState(false);
  const [showBulkResetConfirm, setShowBulkResetConfirm] = useState(false);
  const [showClearGuestsConfirm, setShowClearGuestsConfirm] = useState(false);
  const [bulkActionInProgress, setBulkActionInProgress] = useState<string | null>(null);

  const applyDefaultPrefsChange = useCallback((patch: Partial<DefaultGuestPreferences>) => {
    for (const key of Object.keys(patch) as (keyof DefaultGuestPreferences)[])
      defaultPrefsChangedRef.current.add(key);
    setDefaultGuestPreferences((prev: DefaultGuestPreferences) => ({ ...prev, ...patch }));
  }, []);

  const applyDefaultPrefsPush = useCallback(
    (patch: Partial<DefaultGuestPreferences>) => {
      for (const key of Object.keys(patch) as (keyof DefaultGuestPreferences)[])
        defaultPrefsPushesRef.current.set(key, (defaultPrefsPushesRef.current.get(key) ?? 0) + 1);
      applyDefaultPrefsChange(patch);
    },
    [applyDefaultPrefsChange]
  );

  const applyDefaultPrefsReply = useCallback(
    (
      patch: Partial<DefaultGuestPreferences>,
      pushesBefore: ReadonlyMap<keyof DefaultGuestPreferences, number>
    ) => {
      const unpushed: Partial<DefaultGuestPreferences> = {};
      for (const key of Object.keys(patch) as (keyof DefaultGuestPreferences)[])
        if (defaultPrefsPushesRef.current.get(key) === pushesBefore.get(key))
          Object.assign(unpushed, { [key]: patch[key] });
      applyDefaultPrefsChange(unpushed);
    },
    [applyDefaultPrefsChange]
  );

  // One setting's request; a failure comes back named by `title` instead of thrown, so one refused
  // setting does not stop the others Save sends.
  const attempt = async (
    title: string,
    send: () => Promise<void>
  ): Promise<GuestSaveFailure | null> => {
    try {
      await send();
      return null;
    } catch (reason: unknown) {
      return { title, reason };
    }
  };

  // Helper to update default time format based on a format value.
  //
  // One request, because the three flags are one clock. Sent as three they commit separately: a
  // sibling that fails, or an admin on another tab writing between two of them, leaves the stored
  // default naming a clock nobody picked, and the next guest session seeds from it.
  const updateDefaultTimeFormat = async (format: TimeSettingValue) => {
    const pushesBefore = new Map(defaultPrefsPushesRef.current);
    const response = await fetch(
      '/api/system/default-guest-preferences/clock',
      ApiService.getJsonFetchOptions(clockFromTimeSetting(format), { method: 'PATCH' })
    );

    await assertOk(response);
    // The server normalizes before storing, so the stored clock is read back off the reply rather
    // than assumed from what was sent.
    const { clock } = (await response.json()) as { clock: ClockPreferences };
    applyDefaultPrefsReply(clock, pushesBefore);
  };

  // Get current default time format from the boolean settings behind it.
  const getCurrentDefaultFormat = (): TimeSettingValue =>
    timeSettingFromClock(defaultGuestPreferences);

  const translatedDurationOptions = durationOptions.map(
    (option: { value: string; label: string }) => ({
      ...option,
      label: t(`user.guest.durationOptions.${option.value}`)
    })
  );
  const translatedRefreshRateOptions = refreshRateOptions.map(
    (option: { value: string; label: string }) => ({
      ...option,
      label: t(`user.guest.refreshRates.${option.value}`)
    })
  );
  const prefillDurationOptions = [
    { value: '1', label: t('user.guest.prefillDurationOptions.1') },
    { value: '2', label: t('user.guest.prefillDurationOptions.2') },
    { value: '3', label: t('user.guest.prefillDurationOptions.3') }
  ];
  const maxThreadOptions = getThreadOptions(t);
  const onOffOptions = getOnOffOptions(t);
  const timeFormatOptions = getTimeFormatOptions(t, guestTimeFormatKeys);
  const themeOptions = availableThemes.map((theme: ThemeOption) => ({
    value: theme.id,
    label: theme.name
  }));
  const preferenceLabels = {
    sharpCorners: t('user.guest.preferences.sharpCorners.label'),
    // The row reads "Tooltips" with On meaning shown, so its save error names it the same way
    disableTooltips: t('activeSessions.preferencesModal.tooltips'),
    showDatasourceLabels: t('user.guest.preferences.datasourceLabels.label')
  };

  const loadDefaultGuestPreferences = async () => {
    // Mount, reconnect and Retry can overlap; only the newest request writes values or the error.
    const request = ++defaultPrefsRequestRef.current;
    defaultPrefsChangedRef.current.clear();
    try {
      if (!defaultPrefsLoadedRef.current) setLoadingDefaultPrefs(true);
      const response = await fetch(
        '/api/system/default-guest-preferences',
        ApiService.getFetchOptions()
      );
      await assertOk(response);
      const preferences = (await response.json()) as DefaultGuestPreferencesResponse;
      if (request !== defaultPrefsRequestRef.current) return;
      const loaded: DefaultGuestPreferences = {
        useLocalTimezone: preferences.useLocalTimezone,
        useUtcTimezone: preferences.useUtcTimezone ?? false,
        use24HourFormat: preferences.use24HourFormat,
        sharpCorners: preferences.sharpCorners,
        disableTooltips: preferences.disableTooltips,
        showDatasourceLabels: preferences.showDatasourceLabels,
        allowedTimeFormats: preferences.allowedTimeFormats ?? [...TIME_SETTING_VALUES]
      };
      setDefaultGuestPreferences((prev: DefaultGuestPreferences) => {
        const next: DefaultGuestPreferences = { ...loaded };
        for (const key of defaultPrefsChangedRef.current) Object.assign(next, { [key]: prev[key] });
        return next;
      });
      defaultPrefsLoadedRef.current = true;
      setDefaultPrefsError(null);
    } catch (err) {
      if (request !== defaultPrefsRequestRef.current) return;
      if (!defaultPrefsLoadedRef.current) {
        setDefaultPrefsError(getErrorMessage(err));
      } else {
        // The card keeps the values it loaded; the next reconnect asks again.
        console.error(
          '[GuestConfiguration] Failed to reload guest defaults:',
          getErrorMessage(err)
        );
      }
    } finally {
      if (request === defaultPrefsRequestRef.current) {
        setLoadingDefaultPrefs(false);
      }
    }
  };

  const handleUpdateDefaultGuestPref = async (
    key: keyof typeof preferenceLabels,
    value: boolean
  ): Promise<void> => {
    const pushesBefore = new Map(defaultPrefsPushesRef.current);
    const response = await fetch(
      `/api/system/default-guest-preferences/${key}`,
      ApiService.getJsonFetchOptions({ value }, { method: 'PATCH' })
    );

    await assertOk(response);
    applyDefaultPrefsReply({ [key]: value }, pushesBefore);
  };

  const handleDefaultGuestPreferencesChanged = useCallback(
    (data: DefaultGuestPreferencesChangedEvent) => {
      applyDefaultPrefsPush(data.key === 'clock' ? data.clock : { [data.key]: data.value });
    },
    [applyDefaultPrefsPush]
  );

  const handleAllowedTimeFormatsChanged = useCallback(
    (data: { formats: string[] }) => {
      applyDefaultPrefsPush({ allowedTimeFormats: data.formats });
    },
    [applyDefaultPrefsPush]
  );

  // Each service announces its thread limit under its own field name, and the two
  // anonymous services send none at all, so the value is read through the service's
  // capability rather than off a fixed field.
  const handlePrefillConfigChanged = useCallback(
    (service: PrefillServiceConfig, data: GuestPrefillConfigChangedPayload) => {
      prefillChangedRef.current.add(service.id);
      setPrefillConfigs((prev: Record<GameServiceId, GuestPrefillConfig>) => ({
        ...prev,
        [service.id]: {
          enabledByDefault: data.enabledByDefault,
          durationHours: data.durationHours,
          maxThreadCount: service.supportsMaxThreads
            ? (data[service.configEventThreadField] ?? null)
            : null
        }
      }));
    },
    []
  );

  const handleAllowedFormatsChange = async (
    formats: string[]
  ): Promise<GuestSaveFailure | null> => {
    const listFailure = await attempt(t('user.guest.errors.updateAllowedTimeFormats'), async () => {
      const pushesBefore = new Map(defaultPrefsPushesRef.current);
      const response = await fetch(
        '/api/system/default-guest-preferences/allowed-time-formats',
        ApiService.getJsonFetchOptions({ formats }, { method: 'PATCH' })
      );

      await assertOk(response);
      applyDefaultPrefsReply({ allowedTimeFormats: formats }, pushesBefore);
    });
    if (listFailure !== null) return listFailure;

    // If current default is no longer in allowed list, update to first allowed format.
    // The allowed list is already saved, so a failure here names the default clock save.
    const currentDefault = getCurrentDefaultFormat();
    if (formats.includes(currentDefault) || formats.length === 0) return null;
    return attempt(t('user.guest.errors.updateDefaultTimeFormat'), () =>
      updateDefaultTimeFormat(formats[0] as TimeSettingValue)
    );
  };

  // Guest prefill config load/update, driven by PREFILL_SERVICES. Only services that
  // support a thread cap send or read maxThreadCount; the anonymous ones never carry the
  // field on the wire, so it is omitted from their request body and pinned to null locally.
  // Resolves true only when this service's read answered and was applied.
  const loadPrefillConfig = useCallback(async (service: PrefillServiceConfig, request: number) => {
    try {
      setLoadingPrefillConfigs((prev: Record<GameServiceId, boolean>) => ({
        ...prev,
        [service.id]: true
      }));
      const configResponse = await fetch(service.guestConfigPath, ApiService.getFetchOptions());
      await assertOk(configResponse);
      const config = (await configResponse.json()) as GuestPrefillConfigResponse;
      if (request !== prefillConfigRequestRef.current) return false;
      if (!prefillChangedRef.current.has(service.id)) {
        setPrefillConfigs((prev: Record<GameServiceId, GuestPrefillConfig>) => ({
          ...prev,
          [service.id]: toGuestPrefillConfig(service, config)
        }));
      }
      return true;
    } catch (err) {
      if (request !== prefillConfigRequestRef.current) return false;
      // A change made during the read sits on the panel's starting values, which Save would send.
      setDraftPrefill((prev: Partial<Record<GameServiceId, Partial<GuestPrefillConfig>>>) => {
        const rest = { ...prev };
        delete rest[service.id];
        return rest;
      });
      setPrefillConfigError(getErrorMessage(err));
      return false;
    } finally {
      if (request === prefillConfigRequestRef.current) {
        setLoadingPrefillConfigs((prev: Record<GameServiceId, boolean>) => ({
          ...prev,
          [service.id]: false
        }));
      }
    }
  }, []);

  // Mount, reconnect and Retry each reload every service as one batch, and batches can overlap;
  // only the newest batch writes values, the error or the loading flags. The error box stays up
  // while a Retry runs and clears once every read of the newest batch succeeded.
  const loadPrefillConfigs = useCallback(() => {
    const request = ++prefillConfigRequestRef.current;
    prefillChangedRef.current.clear();
    void Promise.all(
      PREFILL_SERVICES.map((service: PrefillServiceConfig) => loadPrefillConfig(service, request))
    ).then((applied: boolean[]) => {
      if (request === prefillConfigRequestRef.current && !applied.includes(false)) {
        setPrefillConfigError(null);
      }
    });
  }, [loadPrefillConfig]);

  // A service stores its whole row, so the request carries the saved row with the change on top.
  const updatePrefillConfig = async (
    service: PrefillServiceConfig,
    change: Partial<GuestPrefillConfig>
  ): Promise<void> => {
    const next = { ...prefillConfigs[service.id], ...change };
    const body: Record<string, unknown> = {
      enabledByDefault: next.enabledByDefault,
      durationHours: next.durationHours
    };
    if (service.supportsMaxThreads) {
      body.maxThreadCount = next.maxThreadCount;
    }
    const response = await fetch(
      service.guestConfigPath,
      ApiService.getJsonFetchOptions(body, { method: 'POST' })
    );

    await assertOk(response);
    const config = (await response.json()) as GuestPrefillConfigResponse;
    prefillChangedRef.current.add(service.id);
    setPrefillConfigs((prev: Record<GameServiceId, GuestPrefillConfig>) => ({
      ...prev,
      [service.id]: toGuestPrefillConfig(service, config)
    }));
  };

  // The saved host settings, which the host's loads and pushes keep current.
  const savedHost: GuestAccessSettings = {
    guestModeLocked,
    durationHours: guestDuration?.durationHours ?? null,
    theme: defaultGuestTheme,
    refreshRate: defaultGuestRefreshRate,
    refreshRateLocked: guestRefreshRateLocked
  };

  // What the controls draw: the saved values with the changes waiting for Save on top.
  const shownHost: GuestAccessSettings = { ...savedHost, ...draftHost };
  const shownPrefs: DefaultGuestPreferences = { ...defaultGuestPreferences, ...draftPrefs };
  const shownPrefillConfig = (service: PrefillServiceConfig): GuestPrefillConfig => ({
    ...prefillConfigs[service.id],
    ...draftPrefill[service.id]
  });

  // A change back to the saved value is no change, so it leaves the draft.
  const editHost = <K extends keyof GuestAccessSettings>(
    key: K,
    value: GuestAccessSettings[K]
  ): void => {
    setDraftHost((previous: Partial<GuestAccessSettings>) => {
      const rest = { ...previous };
      if (value === savedHost[key]) delete rest[key];
      else rest[key] = value;
      return rest;
    });
  };

  // Formats are compared as sets, so picking the saved formats in another order is no change.
  const editPreference = <K extends DraftedPreference>(
    key: K,
    value: DefaultGuestPreferences[K]
  ): void => {
    const saved = defaultGuestPreferences[key];
    const same =
      Array.isArray(value) && Array.isArray(saved)
        ? value.length === saved.length && value.every((format: string) => saved.includes(format))
        : value === saved;
    setDraftPrefs((previous: Partial<Pick<DefaultGuestPreferences, DraftedPreference>>) => {
      const rest = { ...previous };
      if (same) delete rest[key];
      else rest[key] = value;
      return rest;
    });
  };

  const editPrefill = (
    service: PrefillServiceConfig,
    change: Partial<GuestPrefillConfig>
  ): void => {
    const saved = prefillConfigs[service.id];
    setDraftPrefill((previous: Partial<Record<GameServiceId, Partial<GuestPrefillConfig>>>) => {
      const edits: Partial<GuestPrefillConfig> = { ...previous[service.id], ...change };
      for (const key of Object.keys(change) as (keyof GuestPrefillConfig)[])
        if (edits[key] === saved[key]) delete edits[key];
      const rest = { ...previous };
      if (Object.keys(edits).length === 0) delete rest[service.id];
      else rest[service.id] = edits;
      return rest;
    });
  };

  // Handler callbacks for the prefill service rows
  const handleToggleEnabled = (service: PrefillServiceConfig) => {
    editPrefill(service, { enabledByDefault: !shownPrefillConfig(service).enabledByDefault });
  };

  const handleDurationChange = (service: PrefillServiceConfig, hours: number) => {
    editPrefill(service, { durationHours: hours });
  };

  // Services without a thread cap hide the control entirely, so this can only fire for
  // services that support one; the guard keeps a stray call from posting the field.
  const handleMaxThreadsChange = (service: PrefillServiceConfig, threads: number | null) => {
    if (!service.supportsMaxThreads) return;
    editPrefill(service, { maxThreadCount: threads });
  };

  // How Save stores each host setting, and the title that names its failure.
  const hostSaves: {
    [K in keyof GuestAccessSettings]: {
      title: string;
      send: (value: GuestAccessSettings[K]) => Promise<void>;
    };
  } = {
    guestModeLocked: { title: t('user.errors.updateGuestLock'), send: onToggleGuestLock },
    durationHours: { title: t('user.errors.updateGuestDuration'), send: onDurationChange },
    theme: { title: t('user.errors.updateGuestTheme'), send: onGuestThemeChange },
    refreshRate: { title: t('user.errors.updateGuestRefreshRate'), send: onGuestRefreshRateChange },
    refreshRateLocked: {
      title: t('user.errors.updateGuestRefreshRateLock'),
      send: onGuestRefreshRateLockChange
    }
  };

  const hasChanges =
    Object.keys(draftHost).length > 0 ||
    Object.keys(draftPrefs).length > 0 ||
    Object.keys(draftPrefill).length > 0;

  // One Save sends every changed setting through its own request; a refused one does not stop the
  // rest. A control changed again while Save runs stays a change, and a setting whose save failed
  // keeps its change.
  const saveGuestAccess = async (): Promise<void> => {
    if (saving || authMode !== 'authenticated') return;
    setSaving(true);
    const sentHost = draftHost;
    const sentPrefs = draftPrefs;
    const sentPrefill = draftPrefill;

    const saveHost = async <K extends keyof GuestAccessSettings>(
      key: K,
      value: GuestAccessSettings[K]
    ): Promise<GuestSaveFailure | null> => {
      const failure = await attempt(hostSaves[key].title, () => hostSaves[key].send(value));
      if (failure === null) {
        setDraftHost((previous: Partial<GuestAccessSettings>) => withoutSent(previous, key, value));
      }
      return failure;
    };
    const hostWrites = (Object.keys(sentHost) as (keyof GuestAccessSettings)[]).map(
      <K extends keyof GuestAccessSettings>(key: K) =>
        saveHost(key, sentHost[key] as GuestAccessSettings[K])
    );

    const toggleWrites = (Object.keys(preferenceLabels) as (keyof typeof preferenceLabels)[])
      .filter((key: keyof typeof preferenceLabels) => sentPrefs[key] !== undefined)
      .map(async (key: keyof typeof preferenceLabels): Promise<GuestSaveFailure | null> => {
        const value = sentPrefs[key] === true;
        const failure = await attempt(
          t('user.guest.errors.updateDefault', { label: preferenceLabels[key] }),
          () => handleUpdateDefaultGuestPref(key, value)
        );
        if (failure === null) {
          setDraftPrefs((previous: Partial<Pick<DefaultGuestPreferences, DraftedPreference>>) =>
            withoutSent(previous, key, value)
          );
        }
        return failure;
      });

    const formats = sentPrefs.allowedTimeFormats;
    const formatWrites =
      formats === undefined
        ? []
        : [
            handleAllowedFormatsChange(formats).then(
              (failure: GuestSaveFailure | null): GuestSaveFailure | null => {
                if (failure === null) {
                  setDraftPrefs(
                    (previous: Partial<Pick<DefaultGuestPreferences, DraftedPreference>>) =>
                      withoutSent(previous, 'allowedTimeFormats', formats)
                  );
                }
                return failure;
              }
            )
          ];

    const prefillWrites = PREFILL_SERVICES.flatMap((service: PrefillServiceConfig) => {
      const change = sentPrefill[service.id];
      if (change === undefined) return [];
      return [
        attempt(t('user.guest.prefill.errors.update'), () =>
          updatePrefillConfig(service, change)
        ).then((failure: GuestSaveFailure | null): GuestSaveFailure | null => {
          if (failure === null) {
            setDraftPrefill(
              (previous: Partial<Record<GameServiceId, Partial<GuestPrefillConfig>>>) => {
                const edits: Partial<GuestPrefillConfig> = { ...previous[service.id] };
                for (const key of Object.keys(change) as (keyof GuestPrefillConfig)[])
                  if (edits[key] === change[key]) delete edits[key];
                const rest = { ...previous };
                if (Object.keys(edits).length === 0) delete rest[service.id];
                else rest[service.id] = edits;
                return rest;
              }
            );
          }
          return failure;
        })
      ];
    });

    const failures = await Promise.all([
      ...hostWrites,
      ...toggleWrites,
      ...formatWrites,
      ...prefillWrites
    ]);
    setSaving(false);
    setSaveFailure(failures.find((failure: GuestSaveFailure | null) => failure !== null) ?? null);
  };

  // Refresh guest defaults when SignalR reconnects (catches config events missed during disconnect)
  useReconnectRefetch(isConnected, () => {
    loadDefaultGuestPreferences();
    loadPrefillConfigs();
  });

  useEffect(() => {
    loadDefaultGuestPreferences();

    on('DefaultGuestPreferencesChanged', handleDefaultGuestPreferencesChanged);
    on('AllowedTimeFormatsChanged', handleAllowedTimeFormatsChanged);

    // Bind one subscription per service. The handlers are built here rather than in the
    // table so each one closes over its own service and can be passed to off() unchanged.
    const serviceSubscriptions = PREFILL_SERVICES.map((service: PrefillServiceConfig) => ({
      eventName: service.guestConfigChangedEvent,
      handler: (data: GuestPrefillConfigChangedPayload) => handlePrefillConfigChanged(service, data)
    }));

    loadPrefillConfigs();
    for (const subscription of serviceSubscriptions) {
      on(subscription.eventName, subscription.handler);
    }

    return () => {
      off('DefaultGuestPreferencesChanged', handleDefaultGuestPreferencesChanged);
      off('AllowedTimeFormatsChanged', handleAllowedTimeFormatsChanged);
      for (const subscription of serviceSubscriptions) {
        off(subscription.eventName, subscription.handler);
      }
    };
  }, [
    on,
    off,
    handleDefaultGuestPreferencesChanged,
    handleAllowedTimeFormatsChanged,
    handlePrefillConfigChanged,
    loadPrefillConfigs
  ]);

  const handleBulkResetToDefaults = async () => {
    try {
      setBulkActionInProgress('reset');
      const { affectedCount } = await ApiService.bulkResetSessionsToDefaults<{
        affectedCount: number;
      }>();
      showToast('success', t('user.bulkActions.resetSuccess', { count: affectedCount }));
      setShowBulkResetConfirm(false);
    } catch (err: unknown) {
      notifyError(t('user.bulkActions.errors.resetFailed'), err, {
        logLabel: 'Failed to reset sessions to defaults'
      });
    } finally {
      setBulkActionInProgress(null);
    }
  };

  const handleClearAllGuests = async () => {
    try {
      setBulkActionInProgress('clear');
      const { clearedCount } = await ApiService.bulkClearGuestSessions<{ clearedCount: number }>();
      showToast('success', t('user.bulkActions.clearSuccess', { count: clearedCount }));
      onGuestsChanged();
      setShowClearGuestsConfirm(false);
    } catch (err: unknown) {
      notifyError(t('user.bulkActions.errors.clearFailed'), err, {
        logLabel: 'Failed to clear guest sessions'
      });
    } finally {
      setBulkActionInProgress(null);
    }
  };

  const headerLines: React.ReactNode[] =
    guestDuration === null
      ? []
      : [
          t(
            guestModeLocked
              ? 'user.panes.list.guestAccessLocked'
              : 'user.panes.list.guestAccessOpen',
            { count: guestDuration.durationHours }
          )
        ];

  // Set on this page, set by the operator, or the built-in default: the server says which.
  let sessionLengthHelpKey = 'user.panes.help.lengthDefault';
  if (guestDuration?.source === 'ui') sessionLengthHelpKey = 'user.panes.help.lengthUi';
  else if (guestDuration?.source === 'config')
    sessionLengthHelpKey = 'user.panes.help.lengthConfig';

  // A Reset waiting for Save shows the length the server falls back to.
  const sessionLengthDropdown = guestDuration !== null && (
    <span className="user-settings-dropdown">
      <EnhancedDropdown
        options={translatedDurationOptions}
        value={(shownHost.durationHours ?? guestDuration.envVarValue).toString()}
        onChange={(value: string) => editHost('durationHours', Number(value))}
        disabled={!isAdmin}
        size="md"
        className="w-full"
      />
    </span>
  );

  const prefillServiceRows = PREFILL_SERVICES.map((service: PrefillServiceConfig) => {
    const config = shownPrefillConfig(service);
    const ServiceIcon = service.icon;
    return (
      <React.Fragment key={service.id}>
        <div className="users-svc users-svc--two">
          <ServiceIcon size={20} className="users-svc__icon" />
          <span className="users-svc__name">{service.displayName}</span>
          <ToggleSwitch
            size="md"
            options={onOffOptions}
            value={config.enabledByDefault ? 'on' : 'off'}
            onChange={() => handleToggleEnabled(service)}
          />
          <div className="users-svc__cells">
            <div className="users-svc__cell">
              <span className="users-svc__cell-label">{t('user.panes.prefs.accessLasts')}</span>
              <span className="user-settings-dropdown">
                <EnhancedDropdown
                  options={prefillDurationOptions}
                  value={config.durationHours.toString()}
                  onChange={(value: string) => handleDurationChange(service, Number(value))}
                  size="md"
                  className="w-full"
                  triggerAriaLabel={t('user.panes.prefs.accessLastsFor', {
                    service: service.displayName
                  })}
                />
              </span>
            </div>
            {service.supportsMaxThreads && (
              <div className="users-svc__cell">
                <span className="users-svc__cell-label">{t('user.panes.prefs.threads')}</span>
                <span className="user-settings-dropdown">
                  <EnhancedDropdown
                    options={maxThreadOptions}
                    // '' is the "no limit" option
                    value={config.maxThreadCount != null ? String(config.maxThreadCount) : ''}
                    onChange={(value: string) =>
                      handleMaxThreadsChange(service, value === '' ? null : Number(value))
                    }
                    size="md"
                    className="w-full"
                    triggerAriaLabel={t('user.panes.prefs.threadsFor', {
                      service: service.displayName
                    })}
                  />
                </span>
              </div>
            )}
          </div>
        </div>
        {config.enabledByDefault && (
          <div className="users-svc__warning text-themed-warning flex items-center gap-2">
            <span>{t('user.panes.prefs.everyGuestCan', { service: service.displayName })}</span>
            <HelpPopover
              aria-label={t('user.panes.help.everyGuestAria', { service: service.displayName })}
            >
              <p>
                {t('user.panes.help.everyGuest', {
                  service: service.displayName,
                  duration: t(`user.guest.prefillDurationOptions.${config.durationHours}`)
                })}
              </p>
            </HelpPopover>
          </div>
        )}
      </React.Fragment>
    );
  });

  return (
    <>
      <UserPaneHeader
        title={t('activeSessions.access.title')}
        lines={headerLines}
        actions={
          <RowActionsMenu
            open={menuOpen}
            onOpenChange={setMenuOpen}
            aria-label={t('user.panes.menu.label', { name: t('activeSessions.access.title') })}
          >
            {(close: () => void) => (
              <>
                {guestCount !== 0 && (
                  <>
                    <ActionMenuItem
                      onClick={() => {
                        close();
                        setShowBulkResetConfirm(true);
                      }}
                    >
                      {t('user.bulkActions.buttons.reset')}
                    </ActionMenuItem>
                    <ActionMenuDivider />
                  </>
                )}
                <ActionMenuItem
                  onClick={() => {
                    close();
                    setShowClearGuestsConfirm(true);
                  }}
                >
                  {t('user.bulkActions.buttons.clear')}
                </ActionMenuItem>
              </>
            )}
          </RowActionsMenu>
        }
      />

      <div className="users-pane-body">
        <UserPaneSection title={t('user.panes.sections.guestSignIn')}>
          <UserSettingRow
            label={t('user.panes.prefs.newGuestsCanSignIn')}
            help={
              <HelpPopover aria-label={t('user.panes.help.lockAria')}>
                <p>{t('user.panes.help.lock')}</p>
              </HelpPopover>
            }
          >
            <ToggleSwitch
              size="md"
              options={onOffOptions}
              value={shownHost.guestModeLocked ? 'off' : 'on'}
              onChange={(value: string) => editHost('guestModeLocked', value !== 'on')}
            />
          </UserSettingRow>
          {guestDuration !== null ? (
            <UserSettingRow
              label={t('user.panes.prefs.sessionLength')}
              help={
                <HelpPopover aria-label={t('user.panes.help.lengthAria')}>
                  <p>{t(sessionLengthHelpKey)}</p>
                </HelpPopover>
              }
            >
              {isAdmin && guestDuration.source === 'ui' && shownHost.durationHours !== null && (
                <Button variant="default" size="md" onClick={() => editHost('durationHours', null)}>
                  {t('common.reset')}
                </Button>
              )}
              {isAdmin ? (
                sessionLengthDropdown
              ) : (
                <Tooltip content={t('user.guest.guestDurationToggle.adminRequired')} position="top">
                  {sessionLengthDropdown}
                </Tooltip>
              )}
            </UserSettingRow>
          ) : (
            // A failed first read shows only in the host's error box above the pane
            guestDefaultsError === null && (
              <UserSettingRow label={t('user.panes.prefs.sessionLength')}>
                <LoadingSpinner inline size="sm" />
              </UserSettingRow>
            )
          )}
        </UserPaneSection>

        <UserPaneSection
          title={t('user.panes.sections.guestPrefill')}
          help={
            <HelpPopover aria-label={t('user.panes.help.prefillAria')}>
              <p>{t('user.panes.help.prefill')}</p>
              <p>{t('user.guest.prefill.existingGuestsNote')}</p>
            </HelpPopover>
          }
        >
          {prefillConfigError !== null && (
            <ErrorBlock
              title={t('user.guest.prefill.errors.loadConfig')}
              message={prefillConfigError}
              retryLabel={t('common.retry')}
              onRetry={loadPrefillConfigs}
              retrying={PREFILL_SERVICES.some(
                (service: PrefillServiceConfig) => loadingPrefillConfigs[service.id]
              )}
            />
          )}
          {/* The rows start at default values, so a failed read shows only the box above */}
          {prefillConfigError === null && (
            <>
              <div className="users-svc-head users-svc--two" aria-hidden="true">
                <span>{t('user.panes.prefs.accessLasts')}</span>
                <span>{t('user.panes.prefs.threads')}</span>
              </div>
              {prefillServiceRows}
            </>
          )}
        </UserPaneSection>

        <UserPaneSection title={t('user.panes.sections.guestSees')}>
          {/* The rows start at default values, so a failed read shows the box in their place */}
          {defaultPrefsError !== null ? (
            <ErrorBlock
              title={t('user.guest.errors.loadPreferences')}
              message={defaultPrefsError}
              retryLabel={t('common.retry')}
              onRetry={() => loadDefaultGuestPreferences()}
            />
          ) : (
            <>
              <UserSettingRow label={t('user.guest.sections.defaultTheme')}>
                <span className="user-settings-dropdown">
                  <EnhancedDropdown
                    options={themeOptions}
                    value={shownHost.theme}
                    onChange={(value: string) => editHost('theme', value)}
                    size="md"
                    className="w-full"
                  />
                </span>
              </UserSettingRow>
              <UserSettingRow label={t('user.panes.prefs.refreshRate')}>
                <span className="user-settings-dropdown">
                  <EnhancedDropdown
                    options={translatedRefreshRateOptions}
                    value={shownHost.refreshRate}
                    onChange={(value: string) => editHost('refreshRate', value)}
                    size="md"
                    className="w-full"
                  />
                </span>
              </UserSettingRow>
              <UserSettingRow label={t('user.panes.prefs.mayChangeRate')}>
                <ToggleSwitch
                  size="md"
                  options={onOffOptions}
                  value={shownHost.refreshRateLocked ? 'off' : 'on'}
                  onChange={(value: string) => editHost('refreshRateLocked', value === 'off')}
                />
              </UserSettingRow>
              <UserSettingRow label={t('user.panes.prefs.timeFormats')}>
                <span className="user-settings-dropdown">
                  <MultiSelectDropdown
                    options={timeFormatOptions}
                    values={shownPrefs.allowedTimeFormats}
                    onChange={(formats: string[]) => editPreference('allowedTimeFormats', formats)}
                    placeholder={t('user.guest.timeFormats.placeholder')}
                    minSelections={1}
                    disabled={loadingDefaultPrefs}
                    dropdownWidth="w-80"
                    alignRight
                    className="w-full"
                  />
                </span>
              </UserSettingRow>
              <UserSettingRow label={t('user.guest.preferences.sharpCorners.label')}>
                <ToggleSwitch
                  size="md"
                  options={onOffOptions}
                  value={shownPrefs.sharpCorners ? 'on' : 'off'}
                  disabled={loadingDefaultPrefs}
                  onChange={(value: string) => editPreference('sharpCorners', value === 'on')}
                />
              </UserSettingRow>
              <UserSettingRow label={t('activeSessions.preferencesModal.tooltips')}>
                <ToggleSwitch
                  size="md"
                  options={onOffOptions}
                  value={shownPrefs.disableTooltips ? 'off' : 'on'}
                  disabled={loadingDefaultPrefs}
                  onChange={(value: string) => editPreference('disableTooltips', value !== 'on')}
                />
              </UserSettingRow>
              <UserSettingRow label={t('user.guest.preferences.datasourceLabels.label')}>
                <ToggleSwitch
                  size="md"
                  options={onOffOptions}
                  value={shownPrefs.showDatasourceLabels ? 'on' : 'off'}
                  disabled={loadingDefaultPrefs}
                  onChange={(value: string) =>
                    editPreference('showDatasourceLabels', value === 'on')
                  }
                />
              </UserSettingRow>
            </>
          )}
        </UserPaneSection>
      </div>

      <div className="guest-access-footer">
        {saveFailure !== null && (
          <Alert color="error" title={saveFailure.title}>
            <p className="text-sm">{getErrorMessage(saveFailure.reason)}</p>
          </Alert>
        )}
        <div className="guest-access-footer__row">
          {hasChanges && (
            <span className="text-sm text-themed-secondary">{t('user.panes.unsavedChanges')}</span>
          )}
          <Button
            variant="filled"
            color="primary"
            className="users-pane-btn"
            loading={saving}
            disabled={!hasChanges}
            onClick={() => void saveGuestAccess()}
          >
            {t('common.save')}
          </Button>
        </div>
      </div>

      <ConfirmationModal
        opened={showBulkResetConfirm}
        onClose={() => setShowBulkResetConfirm(false)}
        onConfirm={handleBulkResetToDefaults}
        title={t('user.bulkActions.resetModal.title')}
        icon={<RotateCcw className="w-6 h-6 text-themed-warning" />}
        confirmLabel={t('user.bulkActions.resetModal.confirm')}
        confirmColor="orange"
        loading={bulkActionInProgress === 'reset'}
        confirmDisabled={!!bulkActionInProgress}
      >
        <p className="text-themed-secondary">{t('user.bulkActions.resetModal.message')}</p>

        <Alert color="yellow" icon={null}>
          <p className="text-sm">{t('user.bulkActions.resetModal.summary')}</p>
        </Alert>
      </ConfirmationModal>

      <ConfirmationModal
        opened={showClearGuestsConfirm}
        onClose={() => setShowClearGuestsConfirm(false)}
        onConfirm={handleClearAllGuests}
        title={t('user.bulkActions.clearModal.title')}
        icon={<Eraser className="w-6 h-6 text-themed-error" />}
        confirmLabel={t('user.bulkActions.clearModal.confirm')}
        loading={bulkActionInProgress === 'clear'}
        confirmDisabled={!!bulkActionInProgress}
      >
        <p className="text-themed-secondary">{t('user.bulkActions.clearModal.message')}</p>

        <Alert color="red" icon={null}>
          <p className="text-sm">{t('user.bulkActions.clearModal.summary')}</p>
        </Alert>
      </ConfirmationModal>
    </>
  );
};

export default GuestConfiguration;

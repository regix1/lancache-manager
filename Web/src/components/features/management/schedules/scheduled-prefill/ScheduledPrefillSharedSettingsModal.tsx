import { useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Modal } from '@components/ui/Modal';
import { Button } from '@components/ui/Button';
import { Alert } from '@components/ui/Alert';
import { NumberInput } from '@components/ui/NumberInput';
import { SegmentedControl } from '@components/ui/SegmentedControl';
import { ToggleSwitch } from '@components/ui/ToggleSwitch';
import { CustomScrollbar } from '@components/ui/CustomScrollbar';
import { ConfirmationModal } from '@components/common/ConfirmationModal';
import LoadingSpinner from '@components/common/LoadingSpinner';
import { PERSISTENT_PREFILL_VALIDITY_BOUNDS } from '@components/features/prefill/persistentPrefillConstants';
import ApiService from '@services/api.service';
import { useConnectionLost } from '@hooks/useConnectionLost';
import { useErrorHandler } from '@hooks/useErrorHandler';
import { getErrorMessage, isAbortError } from '@utils/error';
import { SCHEDULED_PREFILL_SERVICE_RUN_ORDER } from './constants';
import type { ScheduledPrefillPersistenceMode } from './types';
import type { useScheduledPrefillContainers } from './useScheduledPrefillContainers';

interface ScheduledPrefillSharedSettingsModalProps {
  opened: boolean;
  containers: ReturnType<typeof useScheduledPrefillContainers>;
  onClose: () => void;
}
export function ScheduledPrefillSharedSettingsModal({
  opened,
  containers,
  onClose
}: ScheduledPrefillSharedSettingsModalProps) {
  const { t } = useTranslation();
  const { notifyError } = useErrorHandler();
  // While the connection banner is up every read fails for that reason, so the banner speaks for
  // the read alerts.
  const connectionLost = useConnectionLost();
  const baseKey = 'management.schedules.services.scheduledPrefill.config';
  const [days, setDays] = useState<number | null>(null);
  const [mode, setMode] = useState<ScheduledPrefillPersistenceMode | null>(null);
  const [overrides, setOverrides] = useState<string[]>([]);
  const [readErrors, setReadErrors] = useState<{ days?: string; mode?: string }>({});
  const [saveErrors, setSaveErrors] = useState<{ days?: string; mode?: string }>({});
  const [saving, setSaving] = useState(false);
  const [clearOpen, setClearOpen] = useState(false);
  const [clearing, setClearing] = useState(false);
  // Clearing stored logins is a choice like the other two settings; it runs only on Save.
  const [clearPending, setClearPending] = useState(false);
  const [clearOutcome, setClearOutcome] = useState<{ failed: boolean; text: string } | null>(null);
  const [discardOpen, setDiscardOpen] = useState(false);
  const saved = useRef<{ days: number | null; mode: ScheduledPrefillPersistenceMode | null }>({
    days: null,
    mode: null
  });
  const dirty = useRef({ days: false, mode: false });
  const revisions = useRef({ days: 0, mode: 0 });
  const daysWrite = useRef({ serial: 0, pending: 0 });
  const modeWrite = useRef({ serial: 0, pending: 0 });
  const session = useRef({ opened, id: 0 });
  if (session.current.opened !== opened) session.current = { opened, id: session.current.id + 1 };

  useEffect(() => {
    if (!opened) return;
    const opening = session.current.id;
    const controller = new AbortController();
    const current = () =>
      !controller.signal.aborted && session.current.opened && session.current.id === opening;
    const daysPending = daysWrite.current.pending > 0;
    const daysSerialAtRead = daysWrite.current.serial;
    const modePending = modeWrite.current.pending > 0;
    const modeSerialAtRead = modeWrite.current.serial;
    // An opening during the validity write must keep the revision that write captured, or the
    // write drops its result and this opening is left with the previous days.
    const daysRevision = daysPending ? revisions.current.days : ++revisions.current.days;
    // An opening during the mode write must keep the revision that write captured, or the
    // write drops its result and this opening is left with the previous mode.
    const modeRevision = modePending ? revisions.current.mode : ++revisions.current.mode;
    dirty.current = {
      days: daysPending ? dirty.current.days : false,
      mode: modePending ? dirty.current.mode : false
    };
    if (!daysPending) setDays(saved.current.days);
    if (!modePending) setMode(saved.current.mode);
    setSaveErrors({});
    if (!daysPending && !modePending) setSaving(false);
    setClearing(false);
    setClearPending(false);
    setClearOpen(false);
    setDiscardOpen(false);
    setClearOutcome(null);
    void ApiService.getPersistentPrefillValidity(controller.signal)
      .then((result) => {
        if (!current()) return;
        setReadErrors((previous) => ({ ...previous, days: undefined }));
        if (revisions.current.days === daysRevision) {
          // A read that left before the validity write stored still has the previous days.
          if (daysWrite.current.serial !== daysSerialAtRead || daysWrite.current.pending > 0)
            return;
          saved.current.days = result.days;
          if (!dirty.current.days) setDays(result.days);
        }
      })
      .catch((error: unknown) => {
        if (current() && revisions.current.days === daysRevision && !isAbortError(error))
          setReadErrors((previous) => ({ ...previous, days: getErrorMessage(error) }));
      });
    void ApiService.getScheduledPrefillConfig(controller.signal)
      .then((result) => {
        if (!current()) return;
        setReadErrors((previous) => ({ ...previous, mode: undefined }));
        if (revisions.current.mode === modeRevision) {
          // A read that left before the mode write stored still has the previous mode.
          if (modeWrite.current.serial === modeSerialAtRead && modeWrite.current.pending === 0) {
            saved.current.mode = result.persistenceMode;
            if (!dirty.current.mode) setMode(result.persistenceMode);
          }
        }
        setOverrides(
          SCHEDULED_PREFILL_SERVICE_RUN_ORDER.filter(
            (key) => result[key].persistenceMode != null
          ).map((key) => t(`${baseKey}.services.${key}`))
        );
      })
      .catch((error: unknown) => {
        if (current() && revisions.current.mode === modeRevision && !isAbortError(error))
          setReadErrors((previous) => ({ ...previous, mode: getErrorMessage(error) }));
      });
    return () => controller.abort();
  }, [opened, t]);

  const close = () => {
    if (clearing) return;
    // The save was already sent, so closing needs no discard choice. The container list
    // reloads when that save finishes, and a failure is reported on the page.
    if (saving) {
      onClose();
      return;
    }
    if (dirty.current.days || dirty.current.mode || clearPending) setDiscardOpen(true);
    else onClose();
  };
  const save = async () => {
    if (saving || clearing) return;
    const opening = session.current.id;
    const current = () => session.current.opened && session.current.id === opening;
    // The logins are cleared first, while the confirm stays open with its own status; a failed
    // clear keeps the choice on and the dialog open so Save can retry it.
    let clearFailed = false;
    if (clearPending) {
      setClearing(true);
      setClearOutcome(null);
      try {
        const results = await containers.clearLogins();
        if (!current()) return;
        const failed = results.filter((result) => result.outcome === 'failed');
        clearFailed = failed.length > 0;
        if (!clearFailed) setClearPending(false);
        setClearOutcome({
          failed: clearFailed,
          text: clearFailed
            ? t(`${baseKey}.settings.clearLogins.partialFailure`, {
                failedCount: failed.length,
                total: results.length,
                services: failed.map((result) => result.service).join(', ')
              })
            : t(`${baseKey}.settings.clearLogins.success`)
        });
      } catch (error: unknown) {
        if (!current()) return;
        clearFailed = true;
        setClearOutcome({
          failed: true,
          text: t(`${baseKey}.settings.clearLogins.failed`, { error: getErrorMessage(error) })
        });
      } finally {
        if (current()) {
          setClearing(false);
          setClearOpen(false);
        }
      }
    }
    setSaving(true);
    const pending: Promise<void>[] = [];
    const closedErrors: string[] = [];
    if (days !== null && saved.current.days !== null && dirty.current.days) {
      revisions.current.days += 1;
      const daysRevision = revisions.current.days;
      daysWrite.current.pending += 1;
      daysWrite.current.serial += 1;
      pending.push(
        ApiService.updatePersistentPrefillValidity({ days })
          .then(() => {
            daysWrite.current.pending -= 1;
            daysWrite.current.serial += 1;
            if (revisions.current.days !== daysRevision) return;
            saved.current.days = days;
            dirty.current.days = false;
            if (!current()) {
              if (session.current.opened) setDays(days);
              return;
            }
            setReadErrors((previous) => ({ ...previous, days: undefined }));
            setSaveErrors((previous) => ({ ...previous, days: undefined }));
          })
          .catch((error: unknown) => {
            daysWrite.current.pending -= 1;
            daysWrite.current.serial += 1;
            if (current())
              setSaveErrors((previous) => ({ ...previous, days: getErrorMessage(error) }));
            else closedErrors.push(getErrorMessage(error));
          })
      );
    }
    if (mode !== null && saved.current.mode !== null && dirty.current.mode) {
      revisions.current.mode += 1;
      const modeRevision = revisions.current.mode;
      modeWrite.current.pending += 1;
      modeWrite.current.serial += 1;
      pending.push(
        ApiService.setScheduledPrefillPersistence(mode)
          .then((result) => {
            modeWrite.current.pending -= 1;
            modeWrite.current.serial += 1;
            if (revisions.current.mode !== modeRevision) return;
            saved.current.mode = result.persistenceMode;
            dirty.current.mode = false;
            if (!current()) {
              if (session.current.opened) setMode(result.persistenceMode);
              return;
            }
            setMode(result.persistenceMode);
            setReadErrors((previous) => ({ ...previous, mode: undefined }));
            setSaveErrors((previous) => ({ ...previous, mode: undefined }));
          })
          .catch((error: unknown) => {
            modeWrite.current.pending -= 1;
            modeWrite.current.serial += 1;
            if (current())
              setSaveErrors((previous) => ({ ...previous, mode: getErrorMessage(error) }));
            else closedErrors.push(getErrorMessage(error));
          })
      );
    }
    await Promise.all(pending);
    void containers.loadPersistentContainers();
    setSaving(false);
    if (!current()) {
      for (const message of closedErrors)
        notifyError(t(`${baseKey}.settings.saveError`, { error: message }));
      return;
    }
    if (!dirty.current.days && !dirty.current.mode && !clearFailed) onClose();
  };
  const modes: ScheduledPrefillPersistenceMode[] = [
    'killOnRestart',
    'keepAcrossRestart',
    'fullPersistence'
  ];
  return (
    <>
      <Modal
        opened={opened}
        onClose={close}
        title={t(`${baseKey}.settings.title`)}
        size="lg"
        bodyFlexLayout
        className="scheduled-prefill-content-dialog"
      >
        <div className="scheduled-prefill-config-modal">
          <div className="scheduled-prefill-config-modal__scroll-area">
            <CustomScrollbar
              maxHeight="none"
              className="scheduled-prefill-config-modal__viewport"
              railPlacement="outer"
              radius="none"
            >
              <div className="scheduled-prefill-config-modal__scroll-content">
                <p className="text-sm text-themed-muted">{t(`${baseKey}.settings.description`)}</p>
                <section className="scheduled-prefill-platform-block">
                  <h3 className="scheduled-prefill-platform-block__title">
                    {t(`${baseKey}.settings.sections.login`)}
                  </h3>
                  <div className="scheduled-prefill-config-modal__settings-list scheduled-prefill-shared-settings__list">
                    <div className="scheduled-prefill-config-modal__setting-row">
                      <div className="scheduled-prefill-config-modal__setting-copy">
                        <label
                          htmlFor="scheduled-prefill-validity"
                          className="scheduled-prefill-config-modal__global-label"
                        >
                          {t(`${baseKey}.settings.persistentValidityLabel`)}
                        </label>
                      </div>
                      <div className="scheduled-prefill-config-modal__setting-actions">
                        <NumberInput
                          id="scheduled-prefill-validity"
                          className="scheduled-prefill-number-cap scheduled-prefill-number-cap--full"
                          value={days ?? PERSISTENT_PREFILL_VALIDITY_BOUNDS.min}
                          min={PERSISTENT_PREFILL_VALIDITY_BOUNDS.min}
                          max={PERSISTENT_PREFILL_VALIDITY_BOUNDS.max}
                          disabled={days === null || saving}
                          onChange={(value) => {
                            if (typeof value !== 'number') return;
                            revisions.current.days += 1;
                            setDays(value);
                            dirty.current.days = value !== saved.current.days;
                            setSaveErrors((previous) => ({ ...previous, days: undefined }));
                          }}
                        />
                      </div>
                    </div>
                    {readErrors.days && !connectionLost && (
                      <Alert color="red" className="scheduled-prefill-shared-settings__feedback">
                        {t(`${baseKey}.settings.loadError`, { error: readErrors.days })}
                      </Alert>
                    )}
                    {saveErrors.days && (
                      <Alert color="red" className="scheduled-prefill-shared-settings__feedback">
                        {t(`${baseKey}.settings.saveError`, { error: saveErrors.days })}
                      </Alert>
                    )}
                  </div>
                </section>
                <section className="scheduled-prefill-platform-block">
                  <h3
                    id="scheduled-prefill-mode"
                    className="scheduled-prefill-platform-block__title"
                  >
                    {t(`${baseKey}.settings.persistenceModeLabel`)}
                  </h3>
                  <div className="scheduled-prefill-shared-settings__list">
                    <div role="group" aria-labelledby="scheduled-prefill-mode">
                      <SegmentedControl
                        fullWidth
                        value={mode ?? ''}
                        options={modes.map((value) => ({
                          value,
                          label: t(`${baseKey}.settings.persistenceMode.${value}`),
                          disabled: mode === null || saving
                        }))}
                        onChange={(value) => {
                          const selected = modes.find((item) => item === value);
                          if (!selected) return;
                          revisions.current.mode += 1;
                          setMode(selected);
                          dirty.current.mode = selected !== saved.current.mode;
                          setSaveErrors((previous) => ({ ...previous, mode: undefined }));
                        }}
                      />
                    </div>
                  </div>
                  {readErrors.mode && !connectionLost && (
                    <Alert color="red">
                      {t(`${baseKey}.settings.loadError`, { error: readErrors.mode })}
                    </Alert>
                  )}
                  {saveErrors.mode && (
                    <Alert color="red">
                      {t(`${baseKey}.settings.saveError`, { error: saveErrors.mode })}
                    </Alert>
                  )}
                  {(saveErrors.days || saveErrors.mode) &&
                    (dirty.current.days || dirty.current.mode) && (
                      <p className="scheduled-prefill-shared-settings__feedback">
                        {t(`${baseKey}.settings.partialSave`)}
                      </p>
                    )}
                  {overrides.length > 0 && (
                    <p className="scheduled-prefill-shared-settings__feedback text-sm text-themed-muted">
                      {t(`${baseKey}.settings.overrideNote`, { services: overrides.join(', ') })}
                    </p>
                  )}
                  {mode === 'fullPersistence' && (
                    <Alert color="yellow">{t(`${baseKey}.settings.persistenceModeWarning`)}</Alert>
                  )}
                </section>
                <section className="scheduled-prefill-platform-block">
                  <h3 className="scheduled-prefill-platform-block__title">
                    {t(`${baseKey}.settings.clearLogins.zoneTitle`)}
                  </h3>
                  <div
                    className="scheduled-prefill-config-modal__settings-list scheduled-prefill-shared-settings__list"
                    role="group"
                    aria-labelledby="scheduled-prefill-clear-logins"
                  >
                    <div className="scheduled-prefill-config-modal__setting-row">
                      <div className="scheduled-prefill-config-modal__setting-copy">
                        <span
                          id="scheduled-prefill-clear-logins"
                          className="scheduled-prefill-config-modal__global-label"
                        >
                          {t(`${baseKey}.settings.clearLogins.button`)}
                        </span>
                        <p className="scheduled-prefill-config-modal__global-help">
                          {t(`${baseKey}.settings.clearLogins.help`)}
                        </p>
                      </div>
                      <div className="scheduled-prefill-config-modal__setting-actions">
                        <ToggleSwitch
                          size="md"
                          options={[
                            {
                              value: 'false',
                              label: t(`${baseKey}.fields.toggleOff`),
                              activeColor: 'default'
                            },
                            {
                              value: 'true',
                              label: t(`${baseKey}.fields.toggleOn`),
                              activeColor: 'warning'
                            }
                          ]}
                          value={clearPending ? 'true' : 'false'}
                          onChange={(value) => setClearPending(value === 'true')}
                          disabled={saving || clearing}
                        />
                      </div>
                    </div>
                  </div>
                  {clearOutcome && (
                    <Alert color={clearOutcome.failed ? 'red' : 'green'}>{clearOutcome.text}</Alert>
                  )}
                </section>
              </div>
            </CustomScrollbar>
          </div>
          <div className="scheduled-prefill-config-modal__actions">
            {saving && (
              <span className="confirmation-modal__status" role="status">
                <LoadingSpinner inline size="xs" />
                {t(`${baseKey}.settings.saving`)}
              </span>
            )}
            <Button onClick={close} disabled={clearing}>
              {t('common.cancel')}
            </Button>
            <Button
              onClick={() => (clearPending ? setClearOpen(true) : void save())}
              disabled={
                saving || clearing || (!dirty.current.days && !dirty.current.mode && !clearPending)
              }
              color="primary"
              variant="filled"
            >
              {t(`${baseKey}.settings.save`)}
            </Button>
          </div>
        </div>
      </Modal>
      <ConfirmationModal
        opened={clearOpen}
        onClose={() => setClearOpen(false)}
        onConfirm={() => void save()}
        title={t(`${baseKey}.settings.clearLogins.confirmTitle`)}
        confirmLabel={t(`${baseKey}.settings.clearLogins.confirmButton`)}
        loading={clearing}
        busyLabel={t(`${baseKey}.settings.clearLogins.clearing`)}
        confirmColor="red"
      >
        <p>{t(`${baseKey}.settings.clearLogins.confirmBody`)}</p>
      </ConfirmationModal>
      <ConfirmationModal
        opened={discardOpen}
        onClose={() => setDiscardOpen(false)}
        onConfirm={() => {
          setDiscardOpen(false);
          onClose();
        }}
        title={t(`${baseKey}.discardChanges.confirmTitle`)}
        confirmLabel={t(`${baseKey}.discardChanges.confirmButton`)}
        cancelLabel={t('common.keepEditing')}
        confirmColor="red"
      >
        <p>{t(`${baseKey}.discardChanges.confirmBody`)}</p>
      </ConfirmationModal>
    </>
  );
}

import { useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Modal } from '@components/ui/Modal';
import { Button } from '@components/ui/Button';
import { Alert } from '@components/ui/Alert';
import { NumberInput } from '@components/ui/NumberInput';
import { SegmentedControl } from '@components/ui/SegmentedControl';
import { CustomScrollbar } from '@components/ui/CustomScrollbar';
import { ConfirmationModal } from '@components/common/ConfirmationModal';
import { PERSISTENT_PREFILL_VALIDITY_BOUNDS } from '@components/features/prefill/persistentPrefillConstants';
import ApiService from '@services/api.service';
import { getErrorMessage, isAbortError } from '@utils/error';
import { SCHEDULED_PREFILL_SERVICE_RUN_ORDER } from './constants';
import type { ScheduledPrefillPersistenceMode } from './types';
import type { useScheduledPrefillContainers } from './useScheduledPrefillContainers';
import { ScheduledPrefillContainerSettings } from './ScheduledPrefillContainerSettings';

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
  const baseKey = 'management.schedules.services.scheduledPrefill.config';
  const [days, setDays] = useState<number | null>(null);
  const [mode, setMode] = useState<ScheduledPrefillPersistenceMode | null>(null);
  const [overrides, setOverrides] = useState<string[]>([]);
  const [readErrors, setReadErrors] = useState<{ days?: string; mode?: string }>({});
  const [saveErrors, setSaveErrors] = useState<{ days?: string; mode?: string }>({});
  const [saving, setSaving] = useState(false);
  const [clearOpen, setClearOpen] = useState(false);
  const [clearing, setClearing] = useState(false);
  const [clearOutcome, setClearOutcome] = useState<{ failed: boolean; text: string } | null>(null);
  const [discardOpen, setDiscardOpen] = useState(false);
  const saved = useRef<{ days: number | null; mode: ScheduledPrefillPersistenceMode | null }>({
    days: null,
    mode: null
  });
  const dirty = useRef({ days: false, mode: false });
  const revisions = useRef({ days: 0, mode: 0 });
  const session = useRef({ opened, id: 0 });
  if (session.current.opened !== opened) session.current = { opened, id: session.current.id + 1 };

  useEffect(() => {
    if (!opened) return;
    const opening = session.current.id;
    const controller = new AbortController();
    const current = () =>
      !controller.signal.aborted && session.current.opened && session.current.id === opening;
    const daysRevision = ++revisions.current.days;
    const modeRevision = ++revisions.current.mode;
    dirty.current = { days: false, mode: false };
    setDays(saved.current.days);
    setMode(saved.current.mode);
    setSaveErrors({});
    setSaving(false);
    setClearing(false);
    setClearOpen(false);
    setDiscardOpen(false);
    setClearOutcome(null);
    void ApiService.getPersistentPrefillValidity(controller.signal)
      .then((result) => {
        if (!current()) return;
        setReadErrors((previous) => ({ ...previous, days: undefined }));
        if (revisions.current.days === daysRevision) {
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
          saved.current.mode = result.persistenceMode;
          if (!dirty.current.mode) setMode(result.persistenceMode);
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
    if (saving || clearing) return;
    if (dirty.current.days || dirty.current.mode) setDiscardOpen(true);
    else onClose();
  };
  const save = async () => {
    if (saving) return;
    const opening = session.current.id;
    const current = () => session.current.opened && session.current.id === opening;
    setSaving(true);
    const pending: Promise<void>[] = [];
    if (days !== null && saved.current.days !== null && dirty.current.days) {
      revisions.current.days += 1;
      pending.push(
        ApiService.updatePersistentPrefillValidity({ days })
          .then(() => {
            if (!current()) return;
            saved.current.days = days;
            dirty.current.days = false;
            setReadErrors((previous) => ({ ...previous, days: undefined }));
            setSaveErrors((previous) => ({ ...previous, days: undefined }));
          })
          .catch((error: unknown) => {
            if (current())
              setSaveErrors((previous) => ({ ...previous, days: getErrorMessage(error) }));
          })
      );
    }
    if (mode !== null && saved.current.mode !== null && dirty.current.mode) {
      revisions.current.mode += 1;
      pending.push(
        ApiService.setScheduledPrefillPersistence(mode)
          .then((result) => {
            if (!current()) return;
            saved.current.mode = result.persistenceMode;
            dirty.current.mode = false;
            setMode(result.persistenceMode);
            setReadErrors((previous) => ({ ...previous, mode: undefined }));
            setSaveErrors((previous) => ({ ...previous, mode: undefined }));
          })
          .catch((error: unknown) => {
            if (current())
              setSaveErrors((previous) => ({ ...previous, mode: getErrorMessage(error) }));
          })
      );
    }
    await Promise.all(pending);
    if (!current()) return;
    setSaving(false);
    void containers.loadPersistentContainers();
    if (!dirty.current.days && !dirty.current.mode) onClose();
  };
  const clearLogins = async () => {
    if (clearing) return;
    const opening = session.current.id;
    const current = () => session.current.opened && session.current.id === opening;
    setClearing(true);
    setClearOutcome(null);
    try {
      const results = await containers.clearLogins();
      if (!current()) return;
      const failed = results.filter((result) => result.outcome === 'failed');
      setClearOutcome({
        failed: failed.length > 0,
        text:
          failed.length > 0
            ? t(`${baseKey}.settings.clearLogins.partialFailure`, {
                failedCount: failed.length,
                total: results.length,
                services: failed.map((result) => result.service).join(', ')
              })
            : t(`${baseKey}.settings.clearLogins.success`)
      });
    } catch (error: unknown) {
      if (current())
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
        className="scheduled-prefill-content-dialog scheduled-prefill-shared-settings-dialog"
      >
        <div className="scheduled-prefill-content-modal">
          <div className="scheduled-prefill-content-modal__scroll-area">
            <CustomScrollbar
              maxHeight="none"
              className="scheduled-prefill-content-modal__viewport"
              radius="none"
            >
              <ScheduledPrefillContainerSettings>
                <p className="text-sm text-themed-muted">{t(`${baseKey}.settings.description`)}</p>
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
                  {readErrors.days && (
                    <Alert color="red" className="scheduled-prefill-shared-settings__feedback">
                      {t(`${baseKey}.settings.loadError`, { error: readErrors.days })}
                    </Alert>
                  )}
                  {saveErrors.days && (
                    <Alert color="red" className="scheduled-prefill-shared-settings__feedback">
                      {t(`${baseKey}.settings.saveError`, { error: saveErrors.days })}
                    </Alert>
                  )}
                  <div className="scheduled-prefill-config-modal__setting-row">
                    <div className="scheduled-prefill-config-modal__setting-copy">
                      <span
                        id="scheduled-prefill-mode"
                        className="scheduled-prefill-config-modal__global-label"
                      >
                        {t(`${baseKey}.settings.persistenceModeLabel`)}
                      </span>
                    </div>
                    <div className="scheduled-prefill-config-modal__setting-actions">
                      <div role="group" aria-labelledby="scheduled-prefill-mode">
                        <SegmentedControl
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
                  </div>
                  {readErrors.mode && (
                    <Alert color="red" className="scheduled-prefill-shared-settings__feedback">
                      {t(`${baseKey}.settings.loadError`, { error: readErrors.mode })}
                    </Alert>
                  )}
                  {saveErrors.mode && (
                    <Alert color="red" className="scheduled-prefill-shared-settings__feedback">
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
                    <Alert color="yellow" className="scheduled-prefill-shared-settings__feedback">
                      {t(`${baseKey}.settings.persistenceModeWarning`)}
                    </Alert>
                  )}
                  <div className="scheduled-prefill-config-modal__setting-row">
                    <div className="scheduled-prefill-config-modal__setting-copy">
                      <h3 className="scheduled-prefill-config-modal__global-label">
                        {t(`${baseKey}.settings.clearLogins.zoneTitle`)}
                      </h3>
                      <p className="scheduled-prefill-config-modal__global-help">
                        {t(`${baseKey}.settings.clearLogins.help`)}
                      </p>
                    </div>
                    <div className="scheduled-prefill-config-modal__setting-actions">
                      <Button
                        onClick={() => setClearOpen(true)}
                        disabled={saving || clearing}
                        loading={clearing}
                      >
                        {t(`${baseKey}.settings.clearLogins.button`)}
                      </Button>
                    </div>
                  </div>
                  {clearOutcome && (
                    <Alert
                      color={clearOutcome.failed ? 'red' : 'green'}
                      className="scheduled-prefill-shared-settings__feedback"
                    >
                      {clearOutcome.text}
                    </Alert>
                  )}
                </div>
              </ScheduledPrefillContainerSettings>
            </CustomScrollbar>
          </div>
          <div className="scheduled-prefill-config-modal__actions">
            <Button onClick={close} disabled={saving || clearing}>
              {t('common.cancel')}
            </Button>
            <Button
              onClick={() => void save()}
              loading={saving}
              stableWidth
              disabled={saving || clearing || (!dirty.current.days && !dirty.current.mode)}
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
        onConfirm={() => void clearLogins()}
        title={t(`${baseKey}.settings.clearLogins.confirmTitle`)}
        confirmLabel={t(`${baseKey}.settings.clearLogins.confirmButton`)}
        loading={clearing}
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
        confirmColor="red"
      >
        <p>{t(`${baseKey}.discardChanges.confirmBody`)}</p>
      </ConfirmationModal>
    </>
  );
}

import { useRef } from 'react';
import { useTranslation } from 'react-i18next';
import { Button } from '@components/ui/Button';
import Badge from '@components/ui/Badge';
import { Tooltip } from '@components/ui/Tooltip';
import {
  ScheduledPrefillDownloadFields,
  ScheduledPrefillNotificationFields,
  ScheduledPrefillScheduleFields
} from './ScheduledPrefillPlatformFields';
import type { ScheduledPrefillSchedule, ScheduledPrefillServiceKey } from './types';

interface ScheduledPrefillPlatformSectionProps {
  serviceKey: ScheduledPrefillServiceKey;
  config: ScheduledPrefillSchedule;
  disabled: boolean;
  gameSelectionLoading: boolean;
  gameSelectionNeedsLogin: boolean;
  onChange: (schedule: ScheduledPrefillSchedule) => void;
  onSelectGames: () => void;
  onClearGames: () => void;
}
export function ScheduledPrefillPlatformSection({
  onSelectGames,
  onClearGames,
  gameSelectionLoading,
  gameSelectionNeedsLogin,
  ...props
}: ScheduledPrefillPlatformSectionProps) {
  const { t } = useTranslation();
  const baseKey = 'management.schedules.services.scheduledPrefill.config';
  const fieldsDisabled = props.disabled || !props.config.enabled;
  const fields = { ...props, disabled: fieldsDisabled };
  const selectGamesRef = useRef<HTMLSpanElement>(null);
  const selectGamesReasonId = `scheduled-prefill-select-games-reason-${props.serviceKey}`;
  const loginReasonActive = gameSelectionNeedsLogin && !fieldsDisabled && !gameSelectionLoading;
  const selectGamesReason = t(`${baseKey}.selectedGames.signInToSelectGames`, {
    actions: t('management.actions.menuLabel'),
    manageContainer: t(`${baseKey}.records.manageContainer`)
  });
  const openGameSelection = () => {
    if (fieldsDisabled || gameSelectionLoading || gameSelectionNeedsLogin) return;
    selectGamesRef.current?.focus({ preventScroll: true });
    onSelectGames();
  };
  return (
    <div
      className={`scheduled-prefill-platform-section__blocks${fieldsDisabled ? ' scheduled-prefill-platform-section--off' : ''}`}
    >
      <section className="scheduled-prefill-platform-block scheduled-prefill-platform-block--schedule">
        <h3 className="scheduled-prefill-platform-block__title">
          {t(`${baseKey}.platforms.sections.schedule`)}
        </h3>
        <div className="scheduled-prefill-config-modal__settings-list">
          <ScheduledPrefillScheduleFields {...fields} />
        </div>
      </section>
      <section className="scheduled-prefill-platform-block scheduled-prefill-platform-block--download">
        <h3 className="scheduled-prefill-platform-block__title">
          {t(`${baseKey}.platforms.sections.download`)}
        </h3>
        <div className="scheduled-prefill-record-games">
          <Tooltip
            content={loginReasonActive ? selectGamesReason : null}
            className="scheduled-prefill-record-games__select-help"
          >
            <span
              ref={selectGamesRef}
              className={`scheduled-prefill-record-games__select-trigger${loginReasonActive ? ' scheduled-prefill-record-games__select-trigger--disabled' : ''}`}
              tabIndex={loginReasonActive ? 0 : -1}
              aria-label={t(`${baseKey}.actions.selectGames`)}
              aria-describedby={loginReasonActive ? selectGamesReasonId : undefined}
            >
              <Button
                className="scheduled-prefill-record-games__select"
                onClick={openGameSelection}
                disabled={fieldsDisabled || gameSelectionLoading || gameSelectionNeedsLogin}
                loading={gameSelectionLoading}
              >
                {t(`${baseKey}.actions.selectGames`)}
                <Badge variant="neutral" className="badge-count">
                  {props.config.selectedAppIds.length}
                </Badge>
              </Button>
              <span id={selectGamesReasonId} className="sr-only">
                {selectGamesReason}
              </span>
            </span>
          </Tooltip>
          <Button
            onClick={onClearGames}
            disabled={fieldsDisabled || props.config.selectedAppIds.length === 0}
          >
            {t(`${baseKey}.actions.clearGames`)}
          </Button>
        </div>
        <div className="scheduled-prefill-config-modal__settings-list">
          <ScheduledPrefillDownloadFields {...fields} />
        </div>
      </section>
      <section className="scheduled-prefill-platform-block scheduled-prefill-platform-block--notifications">
        <h3 className="scheduled-prefill-platform-block__title">
          {t(`${baseKey}.platforms.sections.notifications`)}
        </h3>
        <div className="scheduled-prefill-config-modal__settings-list">
          <ScheduledPrefillNotificationFields {...fields} />
        </div>
      </section>
    </div>
  );
}

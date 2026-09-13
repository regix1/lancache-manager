import { useId, useState, type ReactNode } from 'react';
import { ChevronDown } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { Button } from '@components/ui/Button';
import { Card } from '@components/ui/Card';
import { CollapsibleRegion } from '@components/ui/CollapsibleRegion';
import { useMediaQuery } from '@hooks/useMediaQuery';
import { SCHEDULED_PREFILL_BUTTON_SIZE } from './constants';

interface ScheduledPrefillContainerSettingsProps {
  children: ReactNode;
  disabled: boolean;
}

export function ScheduledPrefillContainerSettings({
  children,
  disabled
}: ScheduledPrefillContainerSettingsProps) {
  const { t } = useTranslation();
  const [open, setOpen] = useState(false);
  const contentId = useId();
  const isMobile = useMediaQuery('(max-width: 767.98px)');
  const visible = !isMobile || open;
  const baseKey = 'management.schedules.services.scheduledPrefill.config';

  return (
    <Card
      padding="md"
      className="scheduled-prefill-platform-block scheduled-prefill-platform-block--container-settings"
    >
      <div className="scheduled-prefill-container-settings__heading">
        <h4 className="scheduled-prefill-platform-block__title">
          {t(`${baseKey}.settings.sharedContainerSettings`)}
        </h4>
        <p className="scheduled-prefill-container-settings__help">
          {t(`${baseKey}.settings.description`)}
        </p>
      </div>
      <Button
        type="button"
        variant="transparent"
        size={SCHEDULED_PREFILL_BUTTON_SIZE}
        className="scheduled-prefill-container-settings__toggle"
        disabled={disabled}
        aria-expanded={visible}
        aria-controls={contentId}
        onClick={() => setOpen((current) => !current)}
        rightSection={
          <ChevronDown
            size={16}
            aria-hidden="true"
            className={`scheduled-prefill-container-settings__icon${
              open ? ' scheduled-prefill-container-settings__icon--open' : ''
            }`}
          />
        }
      >
        <span className="scheduled-prefill-container-settings__toggle-copy">
          <span className="scheduled-prefill-platform-block__title">
            {t(`${baseKey}.settings.sharedContainerSettings`)}
          </span>
          <span className="scheduled-prefill-container-settings__help">
            {t(`${baseKey}.settings.description`)}
          </span>
        </span>
      </Button>
      <CollapsibleRegion
        open={visible}
        contentClassName="scheduled-prefill-container-settings__content"
      >
        <div id={contentId} className="flex flex-col gap-4">
          {children}
        </div>
      </CollapsibleRegion>
    </Card>
  );
}

import { useTranslation } from 'react-i18next';
import FormField from '@components/ui/FormField';
import { TextInput } from '@components/ui/TextInput';
import { ToggleSwitch } from '@components/ui/ToggleSwitch';
import { noAutofill } from '@utils/autofill';
import { ScheduledPrefillPlatformSection } from './ScheduledPrefillPlatformSection';
import type { ScheduledPrefillSchedule, ScheduledPrefillServiceKey } from './types';

interface ScheduledPrefillPlatformsPanelProps {
  serviceKey: ScheduledPrefillServiceKey;
  config: ScheduledPrefillSchedule;
  disabled: boolean;
  gameSelectionLoading: boolean;
  gameSelectionBlocked: 'login' | 'container' | null;
  onChange: (schedule: ScheduledPrefillSchedule) => void;
  onSelectGames: () => void;
  onClearGames: () => void;
}

export function ScheduledPrefillPlatformsPanel(props: ScheduledPrefillPlatformsPanelProps) {
  const { t } = useTranslation();
  const baseKey = 'management.schedules.services.scheduledPrefill.config';
  return (
    <section className="scheduled-prefill-platforms-panel">
      <div className="scheduled-prefill-platforms__identity">
        <div className="scheduled-prefill-platforms__identity-field">
          <FormField label={t(`${baseKey}.records.name`)}>
            {(field) => (
              <TextInput
                {...field}
                {...noAutofill}
                className="scheduled-prefill-platforms__record-name"
                size="md"
                value={props.config.name}
                disabled={props.disabled}
                onChange={(event) => props.onChange({ ...props.config, name: event.target.value })}
              />
            )}
          </FormField>
        </div>
        <div
          className="scheduled-prefill-platforms__identity-field scheduled-prefill-platforms__identity-field--enabled"
          role="group"
          aria-labelledby="scheduled-prefill-enabled-label"
        >
          <span id="scheduled-prefill-enabled-label" className="form-field-label">
            {t(`${baseKey}.platforms.status.enabled`)}
          </span>
          <ToggleSwitch
            size="md"
            value={props.config.enabled ? 'true' : 'false'}
            disabled={props.disabled}
            options={[
              {
                value: 'false',
                label: t(`${baseKey}.platforms.status.disabled`),
                activeColor: 'default'
              },
              {
                value: 'true',
                label: t(`${baseKey}.platforms.status.enabled`),
                activeColor: 'success'
              }
            ]}
            onChange={(value) => props.onChange({ ...props.config, enabled: value === 'true' })}
          />
        </div>
      </div>
      <ScheduledPrefillPlatformSection {...props} />
    </section>
  );
}

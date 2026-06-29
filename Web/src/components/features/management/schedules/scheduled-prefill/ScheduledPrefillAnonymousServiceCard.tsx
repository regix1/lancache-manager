import { useMemo } from 'react';
import { useTranslation } from 'react-i18next';
import Badge from '@components/ui/Badge';
import type { ScheduledPrefillServiceKey } from './types';

interface ScheduledPrefillAnonymousServiceCardProps {
  serviceKey: Extract<ScheduledPrefillServiceKey, 'battleNet' | 'riot'>;
}

export function ScheduledPrefillAnonymousServiceCard({
  serviceKey
}: ScheduledPrefillAnonymousServiceCardProps) {
  const { t } = useTranslation();
  const baseKey = 'management.schedules.services.scheduledPrefill.config';
  const containersKey = `${baseKey}.persistentContainers`;
  const points = useMemo(
    () =>
      t(`${containersKey}.anonymous.${serviceKey}.points`, {
        returnObjects: true
      }) as string[],
    [containersKey, serviceKey, t]
  );

  return (
    <article className="scheduled-prefill-anonymous-card">
      <header className="scheduled-prefill-anonymous-card__header">
        <h4 className="scheduled-prefill-anonymous-card__title">
          {t(`${baseKey}.services.${serviceKey}`)}
        </h4>
        <Badge variant="success">{t(`${containersKey}.anonymous.badge`)}</Badge>
      </header>
      <p className="scheduled-prefill-anonymous-card__description">
        {t(`${containersKey}.anonymous.${serviceKey}.description`)}
      </p>
      <ul className="scheduled-prefill-anonymous-card__list">
        {points.map((point) => (
          <li key={point}>{point}</li>
        ))}
      </ul>
    </article>
  );
}

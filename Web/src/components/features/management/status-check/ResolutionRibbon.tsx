import React from 'react';
import { useTranslation } from 'react-i18next';
import { formatServiceLabel } from './helpers';
import type { RibbonSegment } from './types';

interface ResolutionRibbonProps {
  segments: RibbonSegment[];
  /** Segments only navigate somewhere once per-service results exist. */
  interactive: boolean;
  onSegmentClick: (service: string) => void;
}

const ResolutionRibbon: React.FC<ResolutionRibbonProps> = ({
  segments,
  interactive,
  onSegmentClick
}) => {
  const { t } = useTranslation();

  return (
    <div
      className="status-check-ribbon"
      role="group"
      aria-label={t('management.sections.statusCheck.ribbonLabel')}
    >
      {segments.map((segment) => {
        const label = `${formatServiceLabel(segment.service)} — ${t(
          `management.sections.statusCheck.segmentStatus.${segment.status}`
        )}`;
        return (
          <button
            key={segment.service}
            type="button"
            className={`status-check-ribbon-segment status-check-ribbon-segment--${segment.status}`}
            aria-label={label}
            title={label}
            disabled={!interactive}
            onClick={() => onSegmentClick(segment.service)}
          />
        );
      })}
    </div>
  );
};

export default ResolutionRibbon;

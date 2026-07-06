import React from 'react';
import { useTranslation } from 'react-i18next';
import { Tooltip } from '@components/ui/Tooltip';
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
          <Tooltip
            key={segment.service}
            content={label}
            className="status-check-ribbon-segment-wrap"
          >
            <button
              type="button"
              className={`status-check-ribbon-segment status-check-ribbon-segment--${segment.status}`}
              aria-label={label}
              disabled={!interactive}
              onClick={() => onSegmentClick(segment.service)}
            />
          </Tooltip>
        );
      })}
    </div>
  );
};

export default ResolutionRibbon;

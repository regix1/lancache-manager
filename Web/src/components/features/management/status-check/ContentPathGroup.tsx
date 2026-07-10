import React from 'react';
import '../managementSectionContent.css';
import { useTranslation } from 'react-i18next';
import type { StatusCheckContentPathResult } from '@services/api.service';
import ContentPathRow from './ContentPathRow';

interface ContentPathGroupProps {
  paths: StatusCheckContentPathResult[];
}

const ContentPathGroup: React.FC<ContentPathGroupProps> = ({ paths }) => {
  const { t } = useTranslation();
  const keys = 'management.sections.statusCheck.content';
  const statuses = new Set(paths.map((path) => path.protocolStatus));
  const resultsVary = paths.length > 1 && statuses.size > 1;
  const sortedPaths = [...paths].sort(
    (a, b) => a.host.localeCompare(b.host) || a.pathDisplay.localeCompare(b.pathDisplay)
  );

  if (paths.length === 0) return null;

  return (
    <section className="status-check-content-group" aria-label={t(`${keys}.groupTitle`)}>
      <div className="status-check-content-group-head">
        <div>
          <h4>{t(`${keys}.groupTitle`)}</h4>
          <p>{t(`${keys}.groupHelp`)}</p>
        </div>
        <span className="status-check-content-path-count tabular-nums">
          {t(`${keys}.pathCount`, { count: paths.length })}
        </span>
      </div>
      {resultsVary && <p className="status-check-content-varies">{t(`${keys}.resultsVary`)}</p>}
      <div className="status-check-content-rows mgmt-list">
        {sortedPaths.map((path) => (
          <ContentPathRow
            key={`${path.host}::${path.pathDisplay}::${path.sampleObservedAtUtc ?? ''}`}
            path={path}
          />
        ))}
      </div>
    </section>
  );
};

export default ContentPathGroup;

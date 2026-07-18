import React from 'react';
import '../managementSectionContent.css';
import { useTranslation } from 'react-i18next';
import LoadingSpinner from '@components/common/LoadingSpinner';
import { formatBytes, formatDateTime } from '@utils/formatters';
import type { StatusCheckContentReport } from '@services/api.service';
import { summarizeContentReport } from './contentPathHelpers';

interface ContentPathSummaryProps {
  report: StatusCheckContentReport | null | undefined;
  isRunning: boolean;
}

/** One labeled tile in the content-path readout grid. */
interface ContentStatTile {
  id: string;
  value: number;
  label: string;
  tone: 'success' | 'warning' | 'info' | null;
}

const ContentPathSummary: React.FC<ContentPathSummaryProps> = ({ report, isRunning }) => {
  const { t } = useTranslation();
  const keys = 'management.sections.statusCheck.content';

  let body: React.ReactNode;

  if (isRunning) {
    body = (
      <div className="status-check-content-state" role="status">
        <LoadingSpinner inline size="sm" />
        <span>{t(`${keys}.updating`)}</span>
      </div>
    );
  } else if (!report) {
    body = <p className="status-check-content-state">{t(`${keys}.oldSnapshot`)}</p>;
  } else if (report.availability === 'logMissing') {
    body = <p className="status-check-content-state">{t(`${keys}.logMissing`)}</p>;
  } else if (report.availability === 'unreadable') {
    body = <p className="status-check-content-state">{t(`${keys}.unreadable`)}</p>;
  } else if (report.availability === 'unsupportedFormat') {
    body = <p className="status-check-content-state">{t(`${keys}.unsupportedFormat`)}</p>;
  } else if (report.availability === 'noSamples') {
    body = <p className="status-check-content-state">{t(`${keys}.noSamples`)}</p>;
  } else if ((report.paths ?? []).length === 0) {
    body = (
      <div className="status-check-content-state status-check-content-state--stacked">
        <p>{t(`${keys}.noPaths`)}</p>
        <p>{t(`${keys}.noPathsHelp`)}</p>
      </div>
    );
  } else {
    const counts = summarizeContentReport(report);
    const stats: ContentStatTile[] = [
      {
        id: 'cache',
        value: counts.cacheObserved,
        label: t(`${keys}.summary.cacheObserved`),
        tone: 'success'
      },
      {
        id: 'protocol',
        value: counts.protocolUsable,
        label: t(`${keys}.summary.protocolUsable`),
        tone: 'info'
      },
      {
        id: 'candidate',
        value: counts.httpsOnlyCandidate,
        label: t(`${keys}.summary.httpsOnlyCandidate`),
        tone: 'warning'
      },
      {
        id: 'inconclusive',
        value: counts.inconclusive,
        label: t(`${keys}.summary.inconclusive`),
        tone: null
      }
    ];

    body = (
      <>
        <div className="mgmt-stat-grid mt-3">
          {stats.map((stat) => (
            <div key={stat.id} className="mgmt-stat">
              <p className="mgmt-stat__label">{stat.label}</p>
              <p
                className={`mgmt-stat__value tabular-nums${
                  stat.value === 0
                    ? ' status-check-value--zero'
                    : stat.tone
                      ? ` status-check-value--${stat.tone}`
                      : ''
                }`}
              >
                {stat.value}
              </p>
            </div>
          ))}
        </div>
        <div className="status-check-content-scope">
          <span>
            {report.checkedAtUtc
              ? t(`${keys}.checkedAt`, { time: formatDateTime(report.checkedAtUtc) })
              : t(`${keys}.checkedAtUnknown`)}
          </span>
          <span>
            {t(`${keys}.scanScope`, {
              count: report.paths.length,
              bytes: formatBytes(report.scannedBytes, 1)
            })}
          </span>
          {report.scanTruncated && <span>{t(`${keys}.scanTruncated`)}</span>}
        </div>
      </>
    );
  }

  return (
    <section
      className="status-check-content-summary"
      aria-labelledby="status-check-content-summary-title"
      aria-live="polite"
      aria-busy={isRunning}
    >
      <div className="status-check-content-summary-head">
        <h4 id="status-check-content-summary-title">{t(`${keys}.title`)}</h4>
      </div>
      {body}
    </section>
  );
};

export default ContentPathSummary;

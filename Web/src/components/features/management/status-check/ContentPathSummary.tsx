import React from 'react';
import '../managementSectionContent.css';
import { useTranslation } from 'react-i18next';
import LoadingSpinner from '@components/common/LoadingSpinner';
import { HelpNote, HelpPopover, HelpSection } from '@components/ui/HelpPopover';
import { formatBytes } from '@utils/formatters';
import { useFormattedDateTime } from '@hooks/useFormattedDateTime';
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
  const checkedAtTime = useFormattedDateTime(report?.checkedAtUtc);
  const keys = 'management.sections.statusCheck.content';

  // What the scan covered, for the heading's popover. The two facts are separate lines rather
  // than one run of prose: neither ends in a full stop, so joining them ran the timestamp
  // straight into the sample count. The truncation caveat is a note, not a third fact.
  const hasScanScope = !!report && report.availability === 'available' && report.paths.length > 0;
  const scanFacts = hasScanScope
    ? [
        report.checkedAtUtc
          ? t(`${keys}.checkedAt`, { time: checkedAtTime })
          : t(`${keys}.checkedAtUnknown`),
        t(`${keys}.scanScope`, {
          count: report.paths.length,
          bytes: formatBytes(report.scannedBytes, 1)
        })
      ]
    : [];

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
  } else if (report.availability === 'noSamples') {
    // scannedBytes separates the two no-sample stories: empty logs (nothing to scan) versus
    // traffic that never qualified as a completed download (polls, metadata checks, zero-byte
    // sessions). Bare-metal caches idle at the second state constantly via Windows Update.
    const noSamplesKey = report.scannedBytes > 0 ? 'noSamples' : 'logsEmpty';
    body = <p className="status-check-content-state">{t(`${keys}.${noSamplesKey}`)}</p>;
  } else if (report.paths.length === 0) {
    body = (
      <div className="status-check-content-state status-check-content-state--stacked">
        <p>{t(`${keys}.noPaths`)}</p>
        <p>{t(`${keys}.noPathsHelp`)}</p>
      </div>
    );
  } else {
    const counts = summarizeContentReport(report);
    const allStats: ContentStatTile[] = [
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
    // Every path lands in exactly one protocol bucket, so at least one tile always survives.
    // Dropping the zeros leaves the row carrying only what actually happened.
    const stats = allStats.filter((stat) => stat.value > 0);

    body = (
      <>
        <div className="mgmt-stat-grid mt-3">
          {stats.map((stat) => (
            <div key={stat.id} className="mgmt-stat">
              <p className="mgmt-stat__label caps-label caps-label--sm">{stat.label}</p>
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
        {/* What the scan covered used to be three sentences below the tiles, which gave the card
            a second footer and stacked three lines four pixels apart on a phone. The card already
            prints the run time above, so the scan's own scope belongs behind the heading. */}
        {scanFacts.length > 0 && (
          <HelpPopover position="left" width={320}>
            <HelpSection title={t(`${keys}.title`)} variant="subtle">
              <div className="status-check-scan-facts">
                {scanFacts.map((fact) => (
                  <p key={fact}>{fact}</p>
                ))}
              </div>
            </HelpSection>
            {report?.scanTruncated && <HelpNote type="info">{t(`${keys}.scanTruncated`)}</HelpNote>}
          </HelpPopover>
        )}
      </div>
      {body}
    </section>
  );
};

export default ContentPathSummary;

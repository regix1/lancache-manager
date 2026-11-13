import React from 'react';
import '../managementSectionContent.css';
import { useTranslation } from 'react-i18next';
import { HelpNote, HelpPopover, HelpSection } from '@components/ui/HelpPopover';
import { formatBytes } from '@utils/formatters';
import { useFormattedDateTime } from '@hooks/useFormattedDateTime';
import type { StatusCheckContentReport } from '@services/api.service';
import { summarizeContentReport } from './contentPathHelpers';

interface ContentPathSummaryProps {
  report: StatusCheckContentReport | null | undefined;
  isRunning: boolean;
}

/** One count in the recent-downloads line: a bold number and the word it counts. */
interface ContentStatTile {
  id: string;
  value: number;
  label: string;
  /** The shared `.text-themed-*` color of the number; primary when the count carries no verdict. */
  tone: 'success' | 'warning' | 'info' | 'primary';
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
  // The states with nothing to count yet say so in one line; what to do about it is in the popover.
  const waitsForDownloads =
    !isRunning &&
    !!report &&
    (report.availability === 'noSamples' ||
      (report.availability === 'available' && report.paths.length === 0));

  let body: React.ReactNode;

  // Waiting text, no spinner: the Run Check button and the domain bar already show the run.
  if (isRunning) {
    body = (
      <p className="status-check-content-state" role="status">
        {t(`${keys}.updating`)}
      </p>
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
    body = <p className="status-check-content-state">{t(`${keys}.noPaths`)}</p>;
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
        tone: 'primary'
      }
    ];

    // Every path lands in exactly one protocol bucket, so at least one count always survives.
    // Dropping the zeros leaves the line carrying only what actually happened.
    body = (
      <p className="status-check-content-state status-check-content-counts">
        {allStats
          .filter((stat) => stat.value > 0)
          .map((stat) => (
            <span key={stat.id} className="status-check-content-count">
              <strong className={`tabular-nums text-themed-${stat.tone}`}>{stat.value}</strong>{' '}
              {stat.label}
            </span>
          ))}
      </p>
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
        {/* What the scan covered, and what to do when there is nothing to count yet, belong
            behind the heading: on the card they were a second footer of sentences. */}
        {scanFacts.length > 0 && (
          <HelpPopover position="left" width={320} aria-label={t(`${keys}.scanFactsLabel`)}>
            <HelpSection title={t(`${keys}.title`)} variant="subtle">
              <div className="status-check-scan-facts">
                {scanFacts.map((fact) => (
                  <p key={fact}>{fact}</p>
                ))}
                <p>{t(`${keys}.summaryHelp`)}</p>
              </div>
            </HelpSection>
            {report?.scanTruncated && <HelpNote type="info">{t(`${keys}.scanTruncated`)}</HelpNote>}
          </HelpPopover>
        )}
        {waitsForDownloads && (
          <HelpPopover position="left" width={320} aria-label={t(`${keys}.downloadThenRunLabel`)}>
            {t(`${keys}.downloadThenRun`)}
          </HelpPopover>
        )}
      </div>
      {body}
    </section>
  );
};

export default ContentPathSummary;

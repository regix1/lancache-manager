import React from 'react';
import '../managementSectionContent.css';
import { useTranslation } from 'react-i18next';
import { CheckCircle2, Globe } from 'lucide-react';
import { AccordionSection } from '@components/ui/AccordionSection';
import { useAccordionGroupItem } from '@contexts/AccordionGroupContext';
import { SectionHeaderChip } from '@components/ui/SectionHeaderActions';
import { Tooltip } from '@components/ui/Tooltip';
import { HelpPopover } from '@components/ui/HelpPopover';
import type { StatusCheckContentReport, StatusCheckServiceResult } from '@services/api.service';
import DomainLeafRow from './DomainLeafRow';
import ContentPathGroup from './ContentPathGroup';
import { getContentPathsForService, isVisibleWithProblemsOnly } from './contentPathHelpers';
import { getServiceAccentColor, splitExamples } from './helpers';
import { formatServiceLabel } from '@utils/serviceDisplayName';
import { formatTimestamp } from '@utils/dateTimeFormat';
import { useReaderClock } from '@hooks/useReaderClock';

interface ServiceResultsListProps {
  /** Already sorted problems-first by the parent. */
  services: StatusCheckServiceResult[];
  expandedServices: ReadonlySet<string>;
  onToggle: (service: string) => void;
  problemsOnly: boolean;
  contentReport: StatusCheckContentReport | null | undefined;
  registerRef: (service: string, element: HTMLDivElement | null) => void;
}

const DOMAIN_STATUS_WEIGHT: Record<string, number> = {
  unresolved: 0,
  mismatched: 1,
  unverified: 2,
  blocked: 3,
  resolved: 4
};

interface ServiceResultItemProps {
  service: StatusCheckServiceResult;
  isExpanded: boolean;
  onToggle: (service: string) => void;
  contentReport: StatusCheckContentReport | null | undefined;
  registerRef: (service: string, element: HTMLDivElement | null) => void;
}

// Split out so useAccordionGroupItem (one hook call per registered AccordionSection) can be
// called once per service - it can't be called inline inside the parent's .map(), since the
// number of visible services can change across renders and hooks must run in a stable order.
const ServiceResultItem: React.FC<ServiceResultItemProps> = ({
  service,
  isExpanded,
  onToggle,
  contentReport,
  registerRef
}) => {
  const { t } = useTranslation();
  const clock = useReaderClock();
  const keys = 'management.sections.statusCheck';

  useAccordionGroupItem(`status-check-${service.service}`, isExpanded, () =>
    onToggle(service.service)
  );

  const contentPaths = getContentPathsForService(contentReport, service.service);
  // Deliberately black-holed domains (v1.5) are benign - they leave both the badge's
  // denominator and the wrong-count so a blocked telemetry endpoint never reads as failure.
  const blockedCount = service.domains.filter((domain) => domain.status === 'blocked').length;
  const activeTotal = service.totalCount - blockedCount;
  const wrongCount = activeTotal - service.resolvedCount;
  const badge =
    service.status === 'resolved' ? (
      <SectionHeaderChip variant="success" className="tabular-nums">
        {t(`${keys}.badgeResolved`, {
          resolved: service.resolvedCount,
          total: activeTotal
        })}
      </SectionHeaderChip>
    ) : service.status === 'partial' ? (
      <SectionHeaderChip variant="warning" className="tabular-nums">
        {t(`${keys}.badgePartial`, { wrong: wrongCount, total: activeTotal })}
      </SectionHeaderChip>
    ) : service.status === 'disabled' ? (
      <SectionHeaderChip variant="neutral">{t(`${keys}.badgeDisabled`)}</SectionHeaderChip>
    ) : service.status === 'unverified' ? (
      <SectionHeaderChip variant="info">{t(`${keys}.badgeUnverified`)}</SectionHeaderChip>
    ) : service.domains.some((domain) => domain.status === 'mismatched') ? (
      // DNS answers but with the wrong IPs - a different failure than "not resolving".
      <SectionHeaderChip variant="warning">{t(`${keys}.badgeMismatched`)}</SectionHeaderChip>
    ) : (
      <SectionHeaderChip variant="error">{t(`${keys}.badgeNone`)}</SectionHeaderChip>
    );

  const sortedDomains = [...service.domains].sort(
    (a, b) =>
      (DOMAIN_STATUS_WEIGHT[a.status] ?? 3) - (DOMAIN_STATUS_WEIGHT[b.status] ?? 3) ||
      a.originalEntry.localeCompare(b.originalEntry)
  );

  // Expected cache IPs are the same across a service's domains - show them ONCE here
  // (only where a "wrong IP" tag needs a reference) instead of on every failing row.
  const expectedIps = [...new Set(service.domains.flatMap((domain) => domain.expectedIps))];
  const hasMismatch = service.domains.some((domain) => domain.status === 'mismatched');
  const { shown: expectedShown, moreCount: expectedMore } = splitExamples(expectedIps, 1);
  const expectedLabel =
    expectedShown[0] + (expectedMore > 0 ? ` ${t(`${keys}.ipMore`, { count: expectedMore })}` : '');

  // Names that joined the domain list after every name the DNS server sends to the cache. Each
  // row shows its own date; the instruction is said once here, from the earliest of those dates.
  const addedDates = service.domains
    .map((domain) => domain.addedToListAtUtc)
    .filter((date): date is string => !!date)
    .sort((a, b) => Date.parse(a) - Date.parse(b));

  return (
    <div ref={(element) => registerRef(service.service, element)}>
      {/* No `count` here on purpose: the status badge below already shows
          resolved/total (e.g. "3/3"), so a separate total-count badge next to
          the title would just repeat the same number. */}
      <AccordionSection
        title={formatServiceLabel(service.service)}
        icon={Globe}
        iconColor={getServiceAccentColor(service.service)}
        isExpanded={isExpanded}
        onToggle={() => onToggle(service.service)}
        badge={badge}
      >
        {service.description && (
          <p className="text-xs mb-3 text-themed-muted">{service.description}</p>
        )}
        {service.status === 'disabled' ? (
          <p className="text-sm text-themed-muted status-check-help-line">
            {t(`${keys}.disabledNote`)}
            <HelpPopover width={320} aria-label={t(`${keys}.disabledReasonLabel`)}>
              {t(`${keys}.disabledReason`, { service: service.service.toUpperCase() })}
            </HelpPopover>
          </p>
        ) : (
          <div>
            {hasMismatch && expectedIps.length > 0 && (
              <Tooltip
                content={expectedIps.join(', ')}
                className="status-check-service-expected tabular-nums"
              >
                {t(`${keys}.expectedForService`, { ips: expectedLabel })}
              </Tooltip>
            )}
            {addedDates.length > 0 && (
              <>
                <p className="status-check-domain-note">
                  {t(`${keys}.addedToListService`, {
                    count: addedDates.length,
                    date: formatTimestamp(addedDates[0], {
                      ...clock,
                      style: 'dateOnly',
                      forceYear: false
                    })
                  })}
                </p>
                <p className="status-check-todo status-check-help-line mb-2">
                  {t(`${keys}.addedToListRestart`, { count: addedDates.length })}
                  <HelpPopover width={320} aria-label={t(`${keys}.addedToListRestartLabel`)}>
                    {t(`${keys}.addedToListRestartWhy`)}
                  </HelpPopover>
                </p>
              </>
            )}
            <div className="mgmt-list divided-list">
              {sortedDomains.map((domain) => (
                <div key={domain.originalEntry} className="status-check-domain-item">
                  <DomainLeafRow result={domain} />
                </div>
              ))}
            </div>
          </div>
        )}
        <ContentPathGroup paths={contentPaths} />
      </AccordionSection>
    </div>
  );
};

const ServiceResultsList: React.FC<ServiceResultsListProps> = ({
  services,
  expandedServices,
  onToggle,
  problemsOnly,
  contentReport,
  registerRef
}) => {
  const { t } = useTranslation();
  const keys = 'management.sections.statusCheck';

  // DNS failures remain problems. A path-level HTTPS-only candidate also remains visible even
  // when the service's DNS badge is healthy; inconclusive content paths stay neutral.
  const visibleServices = problemsOnly
    ? services.filter((service) => isVisibleWithProblemsOnly(service, contentReport))
    : services;

  if (visibleServices.length === 0) {
    return (
      <div className="flex items-center gap-2 text-sm text-themed-secondary py-3">
        <CheckCircle2 className="h-4 w-4 flex-shrink-0 text-[var(--theme-success)]" />
        <span>{t(`${keys}.noProblems`)}</span>
      </div>
    );
  }

  return (
    <div className="space-y-2">
      {visibleServices.map((service) => (
        <ServiceResultItem
          key={service.service}
          service={service}
          isExpanded={expandedServices.has(service.service)}
          onToggle={onToggle}
          contentReport={contentReport}
          registerRef={registerRef}
        />
      ))}
    </div>
  );
};

export default ServiceResultsList;

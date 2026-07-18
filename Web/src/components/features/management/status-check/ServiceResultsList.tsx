import React from 'react';
import '../managementSectionContent.css';
import { useTranslation } from 'react-i18next';
import { CheckCircle2, Globe } from 'lucide-react';
import { AccordionSection } from '@components/ui/AccordionSection';
import Badge from '@components/ui/Badge';
import { Tooltip } from '@components/ui/Tooltip';
import { getServiceColorClass } from '@utils/serviceColors';
import type { StatusCheckContentReport, StatusCheckServiceResult } from '@services/api.service';
import DomainLeafRow from './DomainLeafRow';
import ContentPathGroup from './ContentPathGroup';
import { getContentPathsForService, isVisibleWithProblemsOnly } from './contentPathHelpers';
import { formatServiceLabel, getServiceAccentColor, splitExamples } from './helpers';

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
      {visibleServices.map((service) => {
        const contentPaths = getContentPathsForService(contentReport, service.service);
        // Deliberately black-holed domains (v1.5) are benign - they leave both the badge's
        // denominator and the wrong-count so a blocked telemetry endpoint never reads as failure.
        const blockedCount = service.domains.filter((domain) => domain.status === 'blocked').length;
        const activeTotal = service.totalCount - blockedCount;
        const wrongCount = activeTotal - service.resolvedCount;
        const badge =
          service.status === 'resolved' ? (
            <Badge variant="success" className="tabular-nums">
              {t(`${keys}.badgeResolved`, {
                resolved: service.resolvedCount,
                total: activeTotal
              })}
            </Badge>
          ) : service.status === 'partial' ? (
            <Badge variant="warning" className="tabular-nums">
              {t(`${keys}.badgePartial`, { wrong: wrongCount, total: activeTotal })}
            </Badge>
          ) : service.status === 'disabled' ? (
            <Badge variant="neutral">{t(`${keys}.badgeDisabled`)}</Badge>
          ) : service.status === 'unverified' ? (
            <Badge variant="info">{t(`${keys}.badgeUnverified`)}</Badge>
          ) : service.domains.some((domain) => domain.status === 'mismatched') ? (
            // DNS answers but with the wrong IPs - a different failure than "not resolving".
            <Badge variant="warning">{t(`${keys}.badgeMismatched`)}</Badge>
          ) : (
            <Badge variant="error">{t(`${keys}.badgeNone`)}</Badge>
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
          expectedShown[0] +
          (expectedMore > 0 ? ` ${t(`${keys}.ipMore`, { count: expectedMore })}` : '');

        return (
          <div key={service.service} ref={(element) => registerRef(service.service, element)}>
            {/* No `count` here on purpose: the status badge below already shows
                resolved/total (e.g. "3/3"), so a separate total-count badge next to
                the title would just repeat the same number. */}
            <AccordionSection
              title={formatServiceLabel(service.service)}
              icon={Globe}
              iconColor={getServiceAccentColor(service.service)}
              isExpanded={expandedServices.has(service.service)}
              onToggle={() => onToggle(service.service)}
              badge={badge}
            >
              {service.description && (
                <p className="text-xs mb-3">
                  <span className={`font-medium ${getServiceColorClass(service.service)}`}>
                    {formatServiceLabel(service.service)}
                  </span>
                  <span className="text-themed-muted">: {service.description}</span>
                </p>
              )}
              {service.status === 'disabled' ? (
                <p className="text-sm text-themed-muted">
                  {t(`${keys}.disabledNote`, { service: service.service.toUpperCase() })}
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
                  <div className="mgmt-list">
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
      })}
    </div>
  );
};

export default ServiceResultsList;

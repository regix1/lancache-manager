import React from 'react';
import { useTranslation } from 'react-i18next';
import { CheckCircle2, Globe } from 'lucide-react';
import { AccordionSection } from '@components/ui/AccordionSection';
import Badge from '@components/ui/Badge';
import { getServiceColorClass } from '@utils/serviceColors';
import type { StatusCheckServiceResult } from '@services/api.service';
import DomainLeafRow from './DomainLeafRow';
import { formatServiceLabel, getServiceAccentColor, splitExamples } from './helpers';

interface ServiceResultsListProps {
  /** Already sorted problems-first by the parent. */
  services: StatusCheckServiceResult[];
  expandedServices: ReadonlySet<string>;
  onToggle: (service: string) => void;
  problemsOnly: boolean;
  registerRef: (service: string, element: HTMLDivElement | null) => void;
}

const DOMAIN_STATUS_WEIGHT: Record<string, number> = {
  unresolved: 0,
  mismatched: 1,
  unverified: 2,
  resolved: 3
};

const ServiceResultsList: React.FC<ServiceResultsListProps> = ({
  services,
  expandedServices,
  onToggle,
  problemsOnly,
  registerRef
}) => {
  const { t } = useTranslation();
  const keys = 'management.sections.statusCheck';

  // Disabled services are intentionally not cached - never counted as problems. Unverified
  // services stay visible in the problems filter (they need attention: nothing could be verified).
  const visibleServices = problemsOnly
    ? services.filter(
        (service) =>
          service.status === 'partial' ||
          service.status === 'unresolved' ||
          service.status === 'unverified'
      )
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
        const wrongCount = service.totalCount - service.resolvedCount;
        const badge =
          service.status === 'resolved' ? (
            <Badge variant="success" className="tabular-nums">
              {t(`${keys}.badgeResolved`, {
                resolved: service.resolvedCount,
                total: service.totalCount
              })}
            </Badge>
          ) : service.status === 'partial' ? (
            <Badge variant="warning" className="tabular-nums">
              {t(`${keys}.badgePartial`, { wrong: wrongCount, total: service.totalCount })}
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
            <AccordionSection
              title={formatServiceLabel(service.service)}
              count={service.totalCount}
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
                  <span className="text-themed-muted"> — {service.description}</span>
                </p>
              )}
              {service.status === 'disabled' ? (
                <p className="text-sm text-themed-muted">
                  {t(`${keys}.disabledNote`, { service: service.service.toUpperCase() })}
                </p>
              ) : (
                <div className="space-y-2">
                  {hasMismatch && expectedIps.length > 0 && (
                    <p
                      className="status-check-service-expected tabular-nums"
                      title={expectedIps.join(', ')}
                    >
                      {t(`${keys}.expectedForService`, { ips: expectedLabel })}
                    </p>
                  )}
                  {sortedDomains.map((domain) => (
                    <DomainLeafRow key={domain.originalEntry} result={domain} />
                  ))}
                </div>
              )}
            </AccordionSection>
          </div>
        );
      })}
    </div>
  );
};

export default ServiceResultsList;

import type {
  StatusCheckContentPathResult,
  StatusCheckContentReport,
  StatusCheckProtocolOutcome,
  StatusCheckProtocolStatus,
  StatusCheckServiceResult
} from '@services/api.service';

interface ContentPathSummaryCounts {
  cacheObserved: number;
  protocolUsable: number;
  httpsOnlyCandidate: number;
  inconclusive: number;
}

type BadgeVariant = 'warning' | 'info' | 'neutral';

const PROTOCOL_REASON_KEYS: Readonly<Record<string, string>> = {
  noSafePath: 'noSafePath',
  insufficientEdges: 'insufficientEdges',
  edgeDisagreement: 'edgeDisagreement',
  dohUnavailable: 'dohUnavailable',
  dnsUnavailable: 'dnsUnavailable',
  noPublicEdges: 'noPublicEdges',
  tooManyEdges: 'tooManyEdges',
  nonDefinitiveEdges: 'nonDefinitiveEdges',
  timeout: 'timeout',
  unsafeAddress: 'unsafeAddress',
  probeFailed: 'probeFailed',
  wildcardEntry: 'wildcardEntry'
};

export function summarizeContentReport(
  report: StatusCheckContentReport | null | undefined
): ContentPathSummaryCounts {
  const paths = report?.paths ?? [];
  return paths.reduce<ContentPathSummaryCounts>(
    (counts, path) => {
      if (path.cacheEvidence) counts.cacheObserved += 1;
      if (path.protocolStatus === 'bothUsable' || path.protocolStatus === 'httpUsable') {
        counts.protocolUsable += 1;
      } else if (path.protocolStatus === 'httpsOnlyCandidate') {
        counts.httpsOnlyCandidate += 1;
      } else {
        counts.inconclusive += 1;
      }
      return counts;
    },
    { cacheObserved: 0, protocolUsable: 0, httpsOnlyCandidate: 0, inconclusive: 0 }
  );
}

export function getContentPathsForService(
  report: StatusCheckContentReport | null | undefined,
  service: string
): StatusCheckContentPathResult[] {
  const normalizedService = service.toLowerCase();
  return (report?.paths ?? []).filter((path) => path.service.toLowerCase() === normalizedService);
}

function hasHttpsOnlyCandidate(
  report: StatusCheckContentReport | null | undefined,
  service: string
): boolean {
  return getContentPathsForService(report, service).some(
    (path) => path.protocolStatus === 'httpsOnlyCandidate'
  );
}

function isDnsProblem(service: StatusCheckServiceResult): boolean {
  return (
    service.status === 'partial' ||
    service.status === 'unresolved' ||
    service.status === 'unverified'
  );
}

// An origin that has gone HTTPS-only bypasses the cache regardless of DNS - a per-domain
// probe verdict of httpsOnlyCandidate is a problem worth surfacing in the filter.
function hasDomainHttpsOnlyCandidate(service: StatusCheckServiceResult): boolean {
  return service.domains.some(
    (domain) => domain.edgeProbe?.protocolStatus === 'httpsOnlyCandidate'
  );
}

export function isVisibleWithProblemsOnly(
  service: StatusCheckServiceResult,
  report: StatusCheckContentReport | null | undefined
): boolean {
  return (
    isDnsProblem(service) ||
    hasHttpsOnlyCandidate(report, service.service) ||
    hasDomainHttpsOnlyCandidate(service)
  );
}

export function getProtocolStatusVariant(status: StatusCheckProtocolStatus): BadgeVariant {
  switch (status) {
    case 'bothUsable':
    case 'httpUsable':
      return 'info';
    case 'httpsOnlyCandidate':
      return 'warning';
    case 'notRun':
    case 'inconclusive':
      return 'neutral';
  }
}

export function getProtocolStatusTranslationKey(status: StatusCheckProtocolStatus): string {
  return `management.sections.statusCheck.content.protocolStatus.${status}`;
}

export function getProtocolOutcomeTranslationKey(outcome: StatusCheckProtocolOutcome): string {
  return `management.sections.statusCheck.content.protocolOutcome.${outcome}`;
}

export function getProtocolReasonTranslationKey(reason: string | null): string {
  const suffix = reason ? PROTOCOL_REASON_KEYS[reason] : undefined;
  return `management.sections.statusCheck.content.protocolReason.${suffix ?? 'unknown'}`;
}

export function getSafeRedirectScheme(scheme: string | null): 'http' | 'https' | null {
  if (scheme === 'http' || scheme === 'https') return scheme;
  return null;
}

import React, { useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Search } from 'lucide-react';
import { Pagination } from '@components/ui/Pagination';
import { CustomScrollbar } from '@components/ui/CustomScrollbar';
import { Alert } from '@components/ui/Alert';
import { Button } from '@components/ui/Button';
import Badge from '@components/ui/Badge';
import { usePaginatedList } from '@hooks/usePaginatedList';
import { useFormattedDateTime } from '@hooks/useFormattedDateTime';
import { formatBytes } from '@utils/formatters';
import type {
  CorruptedChunkDetail,
  CorruptionCacheSlice,
  CorruptionObservedRange,
  StructuralCorruptionIssue
} from '@/types';
import '../managementSectionContent.css';
import {
  hasOnlyKeys,
  isIsoDate,
  isNonNegativeInteger,
  isOptionalNonNegativeInteger,
  isPlainRecord
} from './corruptionContractValidation';

interface CorruptionChunkListProps {
  chunks: CorruptedChunkDetail[];
}

// A single service can report thousands of corrupted chunks; cap what is mounted per
// page so the expanded row never renders a giant list, and give the search box/pagination
// something to do only once the list is large enough to warrant them.
const CHUNKS_PER_PAGE = 25;

const STRUCTURAL_ISSUES: readonly StructuralCorruptionIssue[] = [
  'empty_cache_file',
  'truncated_cache_header',
  'malformed_cache_header',
  'invalid_payload_offset',
  'truncated_before_payload',
  'cache_key_path_mismatch',
  'payload_length_mismatch',
  'content_range_length_mismatch',
  'content_length_range_conflict'
];

const isObservedRange = (value: unknown): value is CorruptionObservedRange => {
  if (!isPlainRecord(value) || !hasOnlyKeys(value, ['kind', 'start', 'end'])) return false;
  if (value.kind === 'no_range') return value.start == null && value.end == null;
  return (
    value.kind === 'inclusive' &&
    isNonNegativeInteger(value.start) &&
    isNonNegativeInteger(value.end) &&
    value.start <= value.end
  );
};

const isCacheSlice = (value: unknown): value is CorruptionCacheSlice => {
  if (!isPlainRecord(value) || !hasOnlyKeys(value, ['kind', 'start', 'end'])) return false;
  if (value.kind === 'no_range' || value.kind === 'noslice') {
    return value.start == null && value.end == null;
  }
  return (
    value.kind === 'ranged' &&
    isNonNegativeInteger(value.start) &&
    isNonNegativeInteger(value.end) &&
    value.start <= value.end
  );
};

const isCandidateObservation = (value: unknown) =>
  isPlainRecord(value) &&
  hasOnlyKeys(value, [
    'rawUrl',
    'timestamp',
    'clientIp',
    'method',
    'httpStatus',
    'cacheStatus',
    'rawRange',
    'bytesServed'
  ]) &&
  typeof value.rawUrl === 'string' &&
  value.rawUrl.trim().length > 0 &&
  isIsoDate(value.timestamp) &&
  typeof value.clientIp === 'string' &&
  value.clientIp.trim().length > 0 &&
  value.method === 'GET' &&
  (value.httpStatus === 200 || value.httpStatus === 206) &&
  value.cacheStatus === 'MISS' &&
  (value.rawRange == null || (typeof value.rawRange === 'string' && value.rawRange.length > 0)) &&
  isNonNegativeInteger(value.bytesServed);

const isRepeatedMissEvidence = (value: unknown) => {
  if (
    !isPlainRecord(value) ||
    !hasOnlyKeys(value, [
      'kind',
      'rawUrl',
      'normalizedUri',
      'observedRange',
      'cacheSlice',
      'evidenceCount',
      'firstSeen',
      'lastSeen',
      'observations'
    ]) ||
    value.kind !== 'repeated_miss' ||
    typeof value.rawUrl !== 'string' ||
    value.rawUrl.trim().length === 0 ||
    typeof value.normalizedUri !== 'string' ||
    value.normalizedUri.trim().length === 0 ||
    !isObservedRange(value.observedRange) ||
    !isCacheSlice(value.cacheSlice) ||
    !isNonNegativeInteger(value.evidenceCount) ||
    value.evidenceCount === 0 ||
    !isIsoDate(value.firstSeen) ||
    !isIsoDate(value.lastSeen) ||
    !Array.isArray(value.observations) ||
    value.observations.length !== value.evidenceCount ||
    !value.observations.every(isCandidateObservation)
  ) {
    return false;
  }

  const observations = value.observations as Record<string, unknown>[];
  const first = observations[0];
  const last = observations[observations.length - 1];
  const timestamps = observations.map((observation) => Date.parse(observation.timestamp as string));
  const rawRange = typeof first.rawRange === 'string' ? first.rawRange.trim() : '';
  const rangeMatches =
    value.observedRange.kind === 'no_range'
      ? rawRange === '' || rawRange === '-'
      : rawRange === `bytes=${value.observedRange.start}-${value.observedRange.end}`;
  return (
    first.rawUrl === value.rawUrl &&
    first.timestamp === value.firstSeen &&
    last.timestamp === value.lastSeen &&
    timestamps.every((timestamp, index) => index === 0 || timestamp >= timestamps[index - 1]) &&
    rangeMatches
  );
};

const isFingerprint = (
  value: unknown
): value is { dev: number; ino: number; len: number; mtimeNs: number; ctimeNs: number } =>
  isPlainRecord(value) &&
  hasOnlyKeys(value, ['dev', 'ino', 'len', 'mtimeNs', 'ctimeNs']) &&
  [value.dev, value.ino, value.len].every(isNonNegativeInteger) &&
  [value.mtimeNs, value.ctimeNs].every(
    (timestamp) => typeof timestamp === 'number' && Number.isInteger(timestamp)
  );

const isStructuralEvidence = (value: unknown) =>
  isPlainRecord(value) &&
  hasOnlyKeys(value, [
    'kind',
    'issues',
    'cacheKeyEncoding',
    'cacheKey',
    'cacheKeyMd5',
    'cacheVersion',
    'httpStatus',
    'headerStart',
    'bodyStart',
    'fileLength',
    'actualPayloadLength',
    'expectedPayloadLength',
    'contentLength',
    'contentRange',
    'fingerprint',
    'detectedAtUtc'
  ]) &&
  value.kind === 'structural' &&
  Array.isArray(value.issues) &&
  value.issues.length > 0 &&
  new Set(value.issues).size === value.issues.length &&
  value.issues.every((issue) => STRUCTURAL_ISSUES.includes(issue as StructuralCorruptionIssue)) &&
  value.cacheKeyEncoding === 'hex' &&
  typeof value.cacheKey === 'string' &&
  value.cacheKey.length % 2 === 0 &&
  (value.cacheKey.length > 0
    ? /^[a-f0-9]+$/.test(value.cacheKey)
    : value.issues.some((issue) =>
        [
          'empty_cache_file',
          'truncated_cache_header',
          'malformed_cache_header',
          'invalid_payload_offset',
          'truncated_before_payload'
        ].includes(issue as StructuralCorruptionIssue)
      )) &&
  typeof value.cacheKeyMd5 === 'string' &&
  /^[a-f0-9]{32}$/.test(value.cacheKeyMd5) &&
  value.cacheVersion === 5 &&
  (value.httpStatus == null || value.httpStatus === 200 || value.httpStatus === 206) &&
  isOptionalNonNegativeInteger(value.headerStart) &&
  isOptionalNonNegativeInteger(value.bodyStart) &&
  isNonNegativeInteger(value.fileLength) &&
  isOptionalNonNegativeInteger(value.actualPayloadLength) &&
  isOptionalNonNegativeInteger(value.expectedPayloadLength) &&
  isOptionalNonNegativeInteger(value.contentLength) &&
  (value.contentRange == null ||
    (typeof value.contentRange === 'string' && value.contentRange.trim().length > 0)) &&
  isFingerprint(value.fingerprint) &&
  value.fingerprint.len === value.fileLength &&
  isIsoDate(value.detectedAtUtc);

const isCorruptedChunkDetail = (value: unknown): value is CorruptedChunkDetail =>
  isPlainRecord(value) &&
  hasOnlyKeys(value, ['candidateId', 'datasource', 'service', 'exactPaths', 'evidence']) &&
  typeof value.candidateId === 'string' &&
  value.candidateId.trim().length > 0 &&
  typeof value.datasource === 'string' &&
  value.datasource.trim().length > 0 &&
  typeof value.service === 'string' &&
  value.service.trim().length > 0 &&
  Array.isArray(value.exactPaths) &&
  value.exactPaths.length === 1 &&
  typeof value.exactPaths[0] === 'string' &&
  value.exactPaths[0].trim().length > 0 &&
  (isRepeatedMissEvidence(value.evidence) || isStructuralEvidence(value.evidence));

interface EvidenceObservedAtProps {
  firstSeen: string;
  lastSeen: string;
}

const EvidenceObservedAt: React.FC<EvidenceObservedAtProps> = ({ firstSeen, lastSeen }) => {
  const { t } = useTranslation();
  const formattedFirst = useFormattedDateTime(firstSeen, true);
  const formattedLast = useFormattedDateTime(lastSeen, true);

  return (
    <div className="mgmt-evidence__fact">
      <dt>{t('management.corruption.observedAt')}</dt>
      <dd className="tabular-nums">
        {firstSeen === lastSeen
          ? formattedFirst
          : t('management.corruption.observedWindow', {
              first: formattedFirst,
              last: formattedLast
            })}
      </dd>
    </div>
  );
};

const StructuralDetectedAt: React.FC<{ detectedAt: string }> = ({ detectedAt }) => {
  const { t } = useTranslation();
  const formatted = useFormattedDateTime(detectedAt, true);
  return (
    <div className="mgmt-evidence__fact">
      <dt>{t('management.corruption.structural.detectedAt')}</dt>
      <dd className="tabular-nums">{formatted}</dd>
    </div>
  );
};

/**
 * Renders one service's actionable contract-v4 details with strict evidence validation,
 * one shared list, search, pagination, and the house CustomScrollbar.
 */
const CorruptionChunkList: React.FC<CorruptionChunkListProps> = ({ chunks }) => {
  const { t } = useTranslation();
  const [searchQuery, setSearchQuery] = useState('');

  const items = useMemo(() => chunks.filter(isCorruptedChunkDetail), [chunks]);
  const hasInvalidEvidence = items.length !== chunks.length;

  // Search + pagination only earn their space once the list exceeds a page; smaller
  // lists render straight into the scrollbar so tiny services stay uncluttered.
  const enableControls = items.length > CHUNKS_PER_PAGE;

  const filteredChunks = useMemo(() => {
    const query = searchQuery.trim().toLowerCase();
    if (!query) return items;
    return items.filter((chunk) => {
      const evidence = chunk.evidence;
      const methodSearch = t(
        evidence.kind === 'structural'
          ? 'management.corruption.methods.structural.label'
          : 'management.corruption.methods.repeatedMiss.label'
      );
      const evidenceSearch =
        evidence.kind === 'structural'
          ? [
              ...evidence.issues.flatMap((issue) => [
                issue,
                t(`management.corruption.structuralIssues.${issue}.label`)
              ]),
              evidence.cacheKey,
              evidence.cacheKeyMd5,
              evidence.fileLength,
              evidence.actualPayloadLength,
              evidence.expectedPayloadLength,
              evidence.contentLength,
              evidence.contentRange
            ]
          : [
              evidence.rawUrl,
              evidence.normalizedUri,
              evidence.evidenceCount,
              evidence.observedRange.start,
              evidence.observedRange.end,
              evidence.cacheSlice.start,
              evidence.cacheSlice.end
            ];
      const searchable = [
        chunk.candidateId,
        chunk.datasource,
        chunk.service,
        methodSearch,
        ...chunk.exactPaths,
        ...evidenceSearch
      ]
        .filter((value) => value != null)
        .join(' ')
        .toLowerCase();
      return searchable.includes(query);
    });
  }, [items, searchQuery, t]);

  const { page, setPage, totalPages, paginatedItems } = usePaginatedList<CorruptedChunkDetail>({
    items: filteredChunks,
    pageSize: CHUNKS_PER_PAGE,
    resetKey: searchQuery
  });

  const visibleChunks = filteredChunks.length > CHUNKS_PER_PAGE ? paginatedItems : filteredChunks;

  const renderRepeatedMiss = (chunk: CorruptedChunkDetail) => {
    if (chunk.evidence.kind !== 'repeated_miss') return null;
    const evidence = chunk.evidence;
    const observedRange =
      evidence.observedRange.kind === 'inclusive'
        ? `bytes=${evidence.observedRange.start}-${evidence.observedRange.end}`
        : t('management.corruption.noRange');
    const physicalSlice =
      evidence.cacheSlice.kind === 'ranged'
        ? `bytes=${evidence.cacheSlice.start}-${evidence.cacheSlice.end}`
        : t(`management.corruption.sliceKinds.${evidence.cacheSlice.kind}`, {
            defaultValue: evidence.cacheSlice.kind
          });
    return (
      <div key={chunk.candidateId} className="mgmt-evidence">
        <div className="mgmt-evidence__head">
          <div className="mgmt-evidence__ident">
            <code className="mgmt-evidence__exact-value mgmt-evidence__url text-themed-primary">
              {evidence.rawUrl}
            </code>
          </div>
          <div className="mgmt-evidence__status">
            <Badge variant="warning">{t('management.corruption.repeatedMissBadge')}</Badge>
            <span className="mgmt-evidence__count">
              {t('management.corruption.evidenceCount')}{' '}
              <strong className="text-themed-error">{evidence.evidenceCount}</strong>
            </span>
          </div>
        </div>

        <dl className="mgmt-evidence__facts">
          <div className="mgmt-evidence__fact">
            <dt>{t('management.corruption.datasource')}</dt>
            <dd>{chunk.datasource}</dd>
          </div>
          <EvidenceObservedAt firstSeen={evidence.firstSeen} lastSeen={evidence.lastSeen} />
        </dl>

        <details className="mgmt-evidence__technical">
          <summary>{t('management.corruption.cacheMappingDetails')}</summary>
          <dl className="mgmt-evidence__mapping">
            {evidence.normalizedUri !== evidence.rawUrl && (
              <div className="mgmt-evidence__mapping-item mgmt-evidence__mapping-item--wide">
                <dt>{t('management.corruption.normalizedUri')}</dt>
                <dd>
                  <code className="mgmt-evidence__exact-value">{evidence.normalizedUri}</code>
                </dd>
              </div>
            )}
            <div className="mgmt-evidence__mapping-item">
              <dt>{t('management.corruption.observedRange')}</dt>
              <dd>
                <code>{observedRange}</code>
              </dd>
            </div>
            <div className="mgmt-evidence__mapping-item">
              <dt>{t('management.corruption.physicalSlice')}</dt>
              <dd>
                <code>{physicalSlice}</code>
              </dd>
            </div>
            <div className="mgmt-evidence__mapping-item mgmt-evidence__mapping-item--wide">
              <dt>{t('management.corruption.cache')}</dt>
              <dd>
                {chunk.exactPaths.map((path) => (
                  <code key={path} className="mgmt-evidence__exact-value">
                    {path}
                  </code>
                ))}
              </dd>
            </div>
          </dl>
        </details>
      </div>
    );
  };

  const renderStructural = (chunk: CorruptedChunkDetail) => {
    if (chunk.evidence.kind !== 'structural') return null;
    const evidence = chunk.evidence;
    return (
      <div key={chunk.candidateId} className="mgmt-evidence">
        <div className="mgmt-evidence__head">
          <div className="mgmt-evidence__ident">
            <code className="mgmt-evidence__exact-value mgmt-evidence__url text-themed-primary">
              {chunk.exactPaths[0]}
            </code>
          </div>
          <div className="mgmt-evidence__status">
            {evidence.issues.map((issue) => (
              <Badge key={issue} variant="error">
                {t(`management.corruption.structuralIssues.${issue}.label`)}
              </Badge>
            ))}
          </div>
        </div>

        <dl className="mgmt-evidence__facts">
          <div className="mgmt-evidence__fact">
            <dt>{t('management.corruption.datasource')}</dt>
            <dd>{chunk.datasource}</dd>
          </div>
          <StructuralDetectedAt detectedAt={evidence.detectedAtUtc} />
          {evidence.actualPayloadLength != null && (
            <div className="mgmt-evidence__fact">
              <dt>{t('management.corruption.structural.actualPayload')}</dt>
              <dd className="tabular-nums">{formatBytes(evidence.actualPayloadLength)}</dd>
            </div>
          )}
          {evidence.expectedPayloadLength != null && (
            <div className="mgmt-evidence__fact">
              <dt>{t('management.corruption.structural.expectedPayload')}</dt>
              <dd className="tabular-nums">{formatBytes(evidence.expectedPayloadLength)}</dd>
            </div>
          )}
        </dl>

        <div className="mgmt-evidence__reason">
          {evidence.issues.map((issue) => (
            <p key={issue}>
              <strong>{t(`management.corruption.structuralIssues.${issue}.label`)}</strong>
              <span>{t(`management.corruption.structuralIssues.${issue}.description`)}</span>
            </p>
          ))}
        </div>

        <details className="mgmt-evidence__technical">
          <summary>{t('management.corruption.structural.technicalDetails')}</summary>
          <dl className="mgmt-evidence__mapping">
            <div className="mgmt-evidence__mapping-item mgmt-evidence__mapping-item--wide">
              <dt>{t('management.corruption.structural.exactPath')}</dt>
              <dd>
                <code className="mgmt-evidence__exact-value">{chunk.exactPaths[0]}</code>
              </dd>
            </div>
            <div className="mgmt-evidence__mapping-item mgmt-evidence__mapping-item--wide">
              <dt>{t('management.corruption.structural.cacheKey')}</dt>
              <dd>
                <code className="mgmt-evidence__exact-value">{evidence.cacheKey || '—'}</code>
              </dd>
            </div>
            <div className="mgmt-evidence__mapping-item">
              <dt>{t('management.corruption.structural.fileLength')}</dt>
              <dd>{formatBytes(evidence.fileLength)}</dd>
            </div>
            {evidence.httpStatus != null && (
              <div className="mgmt-evidence__mapping-item">
                <dt>{t('management.corruption.structural.httpStatus')}</dt>
                <dd>{evidence.httpStatus}</dd>
              </div>
            )}
            {evidence.headerStart != null && (
              <div className="mgmt-evidence__mapping-item">
                <dt>{t('management.corruption.structural.headerStart')}</dt>
                <dd>{formatBytes(evidence.headerStart)}</dd>
              </div>
            )}
            {evidence.bodyStart != null && (
              <div className="mgmt-evidence__mapping-item">
                <dt>{t('management.corruption.structural.payloadOffset')}</dt>
                <dd>{formatBytes(evidence.bodyStart)}</dd>
              </div>
            )}
            {evidence.contentLength != null && (
              <div className="mgmt-evidence__mapping-item">
                <dt>{t('management.corruption.structural.contentLength')}</dt>
                <dd>{formatBytes(evidence.contentLength)}</dd>
              </div>
            )}
            {evidence.contentRange && (
              <div className="mgmt-evidence__mapping-item">
                <dt>{t('management.corruption.structural.contentRange')}</dt>
                <dd>
                  <code>{evidence.contentRange}</code>
                </dd>
              </div>
            )}
            <div className="mgmt-evidence__mapping-item">
              <dt>{t('management.corruption.structural.cacheVersion')}</dt>
              <dd>{evidence.cacheVersion ?? '—'}</dd>
            </div>
            <div className="mgmt-evidence__mapping-item">
              <dt>{t('management.corruption.structural.cacheKeyMd5')}</dt>
              <dd>
                <code>{evidence.cacheKeyMd5}</code>
              </dd>
            </div>
          </dl>
        </details>
      </div>
    );
  };

  const renderChunk = (chunk: CorruptedChunkDetail) =>
    chunk.evidence.kind === 'structural' ? renderStructural(chunk) : renderRepeatedMiss(chunk);

  if (hasInvalidEvidence) {
    return (
      <Alert color="red">
        <p className="text-sm">{t('management.corruption.errors.unsafeDetails')}</p>
      </Alert>
    );
  }

  if (items.length === 0) {
    return (
      <p className="py-4 text-center text-sm text-themed-muted">
        {t('management.corruption.noDetailsAvailable')}
      </p>
    );
  }

  return (
    <div className="space-y-3">
      {enableControls && (
        <div className="space-y-1">
          <label htmlFor="corruption-file-search" className="block text-xs text-themed-secondary">
            {t('management.corruption.searchLabel')}
          </label>
          <div className="relative">
            <Search className="input-icon absolute left-3 top-1/2 -translate-y-1/2 w-4 h-4 text-themed-muted" />
            <input
              id="corruption-file-search"
              type="text"
              placeholder={t('management.corruption.searchPlaceholder')}
              value={searchQuery}
              onChange={(e) => setSearchQuery(e.target.value)}
              className="themed-input min-h-11 w-full pl-10 pr-14 py-2 text-sm"
            />
            {searchQuery && (
              <button
                type="button"
                onClick={() => setSearchQuery('')}
                aria-label={t('management.corruption.clearSearch')}
                className="mgmt-search-clear absolute right-0 top-1/2 flex h-11 min-h-11 w-11 min-w-11 -translate-y-1/2 items-center justify-center rounded-lg text-xs text-themed-muted transition-[color] duration-150 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-[var(--theme-border-focus)]"
              >
                {t('common.clear')}
              </button>
            )}
          </div>
          <p className="text-xs text-themed-muted tabular-nums">
            {t('management.corruption.searchResultCount', {
              count: filteredChunks.length,
              total: items.length
            })}
          </p>
        </div>
      )}

      {filteredChunks.length === 0 ? (
        <div className="py-6 text-center text-sm text-themed-muted space-y-3">
          <p>{t('management.corruption.noSearchMatch', { query: searchQuery.trim() })}</p>
          <Button size="sm" onClick={() => setSearchQuery('')}>
            {t('management.corruption.clearSearch')}
          </Button>
        </div>
      ) : (
        <>
          <CustomScrollbar maxHeight="24rem" radius="none" paddingMode="compact">
            <div className="mgmt-evidence-list">{visibleChunks.map(renderChunk)}</div>
          </CustomScrollbar>
          {filteredChunks.length > CHUNKS_PER_PAGE && (
            <Pagination
              currentPage={page}
              totalPages={totalPages}
              totalItems={filteredChunks.length}
              itemsPerPage={CHUNKS_PER_PAGE}
              onPageChange={setPage}
              itemLabel={t('management.corruption.chunkLabel')}
              variant="compact"
              showCard={false}
            />
          )}
        </>
      )}
    </div>
  );
};

export default CorruptionChunkList;

import React, { useId, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { ChevronDown } from 'lucide-react';
import Badge from '@components/ui/Badge';
import { Button } from '@components/ui/Button';
import { CollapsibleRegion } from '@components/ui/CollapsibleRegion';
import { formatBytes, formatRelativeTime } from '@utils/formatters';
import type { StatusCheckContentPathResult } from '@services/api.service';
import ProtocolEdgeList from './ProtocolEdgeList';
import {
  getProtocolReasonTranslationKey,
  getProtocolStatusTranslationKey,
  getProtocolStatusVariant
} from './contentPathHelpers';

interface ContentPathRowProps {
  path: StatusCheckContentPathResult;
}

const ContentPathRow: React.FC<ContentPathRowProps> = ({ path }) => {
  const { t } = useTranslation();
  const keys = 'management.sections.statusCheck.content';
  const [edgesOpen, setEdgesOpen] = useState(false);
  const rowId = useId().replace(/:/g, '');
  const hostId = `status-check-content-host-${rowId}`;
  const regionId = `status-check-content-edges-${rowId}`;
  const evidence = path.cacheEvidence;
  const reasonKey = getProtocolReasonTranslationKey(path.protocolReason);

  return (
    <article className="status-check-content-row" aria-labelledby={hostId}>
      <div className="status-check-content-row-head">
        <div className="status-check-content-identity">
          <h5 id={hostId} className="status-check-content-host">
            {path.host}
          </h5>
          <code className="status-check-content-path">{path.pathDisplay}</code>
        </div>
        <div className="status-check-content-badges" aria-label={t(`${keys}.evidenceLabels`)}>
          <Badge variant={evidence ? 'success' : 'neutral'}>
            {evidence ? t(`${keys}.cacheObservedBadge`) : t(`${keys}.cacheNotObservedBadge`)}
          </Badge>
          <Badge variant={getProtocolStatusVariant(path.protocolStatus)}>
            {t(getProtocolStatusTranslationKey(path.protocolStatus))}
          </Badge>
        </div>
      </div>

      <div className="status-check-content-details">
        <p>
          {evidence
            ? t(`${keys}.cacheEvidence.${evidence.outcome}`, {
                status: evidence.statusCode,
                bytes: formatBytes(evidence.bytes, 1)
              })
            : t(`${keys}.cacheEvidence.none`)}
        </p>
        <p>
          {path.sampleObservedAtUtc
            ? t(`${keys}.sampleObserved`, {
                age: formatRelativeTime(path.sampleObservedAtUtc)
              })
            : t(`${keys}.sampleUnknown`)}
        </p>
        <p>{t(`${keys}.protocolDetail.${path.protocolStatus}`)}</p>
        {path.protocolReason &&
          (path.protocolStatus === 'inconclusive' || path.protocolStatus === 'notRun') && (
            <p>{t(reasonKey)}</p>
          )}
        <p>
          {t(`${keys}.edgeConsensus`, {
            consensus: path.consensusEdges,
            total: path.totalPublicEdges
          })}
        </p>
      </div>

      {path.edges.length > 0 ? (
        <div className="status-check-content-edge-disclosure">
          <Button
            type="button"
            variant="subtle"
            color="gray"
            size="sm"
            className="status-check-content-edge-toggle"
            aria-expanded={edgesOpen}
            aria-controls={regionId}
            rightSection={
              <ChevronDown
                aria-hidden="true"
                className={`status-check-content-edge-chevron${edgesOpen ? ' is-open' : ''}`}
              />
            }
            onClick={() => setEdgesOpen((open) => !open)}
          >
            {edgesOpen
              ? t(`${keys}.hideEdges`)
              : t(`${keys}.viewEdges`, { count: path.edges.length })}
          </Button>
          <CollapsibleRegion
            open={edgesOpen}
            className="status-check-content-edge-region"
            contentClassName="status-check-content-edge-list"
          >
            <div id={regionId}>
              <ProtocolEdgeList edges={path.edges} />
            </div>
          </CollapsibleRegion>
        </div>
      ) : (
        <p className="status-check-content-no-edges">{t(`${keys}.noTestedEdges`)}</p>
      )}
    </article>
  );
};

export default ContentPathRow;

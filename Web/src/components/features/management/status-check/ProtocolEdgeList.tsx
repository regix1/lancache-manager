import React from 'react';
import { useTranslation } from 'react-i18next';
import Badge from '@components/ui/Badge';
import type {
  StatusCheckContentPathEdgeResult,
  StatusCheckProtocolProbeResult
} from '@services/api.service';
import { getProtocolOutcomeTranslationKey, getSafeRedirectScheme } from './contentPathHelpers';

interface ProtocolOutcomeProps {
  label: string;
  result: StatusCheckProtocolProbeResult;
}

const ProtocolOutcome: React.FC<ProtocolOutcomeProps> = ({ label, result }) => {
  const { t } = useTranslation();
  const keys = 'management.sections.statusCheck.content';
  const redirectScheme = getSafeRedirectScheme(result.redirectScheme);

  return (
    <div className="status-check-content-protocol-outcome">
      <span className="status-check-content-protocol-label">{label}</span>
      <span className="status-check-content-protocol-result">
        {t(getProtocolOutcomeTranslationKey(result.outcome))}
        {result.statusCode !== null &&
          ` · ${t(`${keys}.statusCode`, { status: result.statusCode })}`}
        {redirectScheme && ` · ${t(`${keys}.redirectScheme`, { scheme: redirectScheme })}`}
      </span>
    </div>
  );
};

interface ProtocolEdgeListProps {
  edges: StatusCheckContentPathEdgeResult[];
}

/** The per-edge HTTP/HTTPS outcome blocks shared by the content-path rows and the
 *  test-a-domain probe result. */
const ProtocolEdgeList: React.FC<ProtocolEdgeListProps> = ({ edges }) => {
  const { t } = useTranslation();
  const keys = 'management.sections.statusCheck.content';

  return (
    <>
      {edges.map((edge, index) => (
        <div
          key={`${edge.address}-${edge.addressFamily}-${index}`}
          className="status-check-content-edge"
        >
          <div className="status-check-content-edge-head">
            <span className="status-check-content-edge-address tabular-nums">{edge.address}</span>
            <Badge variant="neutral">{t(`${keys}.addressFamily.${edge.addressFamily}`)}</Badge>
          </div>
          <ProtocolOutcome label="HTTP" result={edge.http} />
          <ProtocolOutcome label="HTTPS" result={edge.https} />
        </div>
      ))}
    </>
  );
};

export default ProtocolEdgeList;

import React from 'react';
import { useTranslation } from 'react-i18next';
import { MonitorSmartphone } from 'lucide-react';
import { Card } from '@components/ui/Card';
import { Button } from '@components/ui/Button';
import LoadingSpinner from '@components/common/LoadingSpinner';
import { CLIENT_PROBE_HOST, CLIENT_PROBE_TIMEOUT_MS } from './constants';
import type { ClientProbeState, ClientProbeStatus } from './types';

interface ClientProbeCardProps {
  state: ClientProbeState;
  onRetry: () => void;
}

const GLYPH_CLASS_BY_STATUS: Record<ClientProbeStatus, string> = {
  checking: 'status-check-glyph--neutral',
  intercepted: 'status-check-glyph--success',
  inconclusive: 'status-check-glyph--warning',
  unreachable: 'status-check-glyph--error',
  blocked: 'status-check-glyph--info'
};

const DETAIL_BOX_CLASS_BY_STATUS: Record<ClientProbeStatus, string> = {
  checking: '',
  intercepted: 'bg-[var(--theme-success-bg)] text-[var(--theme-success-text)]',
  inconclusive: 'bg-[var(--theme-warning-bg)] text-[var(--theme-warning-text)]',
  unreachable: 'bg-[var(--theme-error-bg)] text-[var(--theme-error-text)]',
  blocked: 'bg-[var(--theme-info-bg)] text-[var(--theme-info-text)]'
};

const ClientProbeCard: React.FC<ClientProbeCardProps> = ({ state, onRetry }) => {
  const { t } = useTranslation();
  const keys = 'management.sections.statusCheck';

  const titleByStatus: Record<ClientProbeStatus, string> = {
    checking: t(`${keys}.probeChecking`),
    intercepted: t(`${keys}.probeIntercepted`),
    inconclusive: t(`${keys}.probeInconclusive`),
    unreachable: t(`${keys}.probeUnreachable`),
    blocked: t(`${keys}.probeBlocked`)
  };

  const detailByStatus: Record<ClientProbeStatus, string> = {
    checking: '',
    intercepted: t(`${keys}.probeInterceptedDetail`, { host: state.servedBy }),
    inconclusive: t(`${keys}.probeInconclusiveDetail`, { domain: CLIENT_PROBE_HOST }),
    unreachable: t(`${keys}.probeUnreachableDetail`, {
      domain: CLIENT_PROBE_HOST,
      seconds: CLIENT_PROBE_TIMEOUT_MS / 1000
    }),
    blocked: t(`${keys}.probeBlockedDetail`)
  };

  return (
    <Card>
      <div className="flex flex-col gap-3 sm:flex-row sm:items-start sm:justify-between">
        <div className="flex items-start gap-3 min-w-0">
          <div className={`status-check-glyph ${GLYPH_CLASS_BY_STATUS[state.status]}`}>
            <MonitorSmartphone className="w-5 h-5" />
          </div>
          <div className="min-w-0">
            <p className="font-medium text-themed-primary flex items-center gap-2">
              {state.status === 'checking' && <LoadingSpinner inline size="sm" />}
              {titleByStatus[state.status]}
            </p>
            <p className="text-xs text-themed-muted">{t(`${keys}.deviceLaneDesc`)}</p>
          </div>
        </div>
        {state.status !== 'checking' && state.status !== 'blocked' && (
          <Button variant="subtle" size="sm" className="flex-shrink-0" onClick={onRetry}>
            {t(`${keys}.probeRetry`)}
          </Button>
        )}
      </div>
      {state.status !== 'checking' && detailByStatus[state.status] && (
        <div
          className={`mt-3 text-xs p-2.5 rounded leading-relaxed ${DETAIL_BOX_CLASS_BY_STATUS[state.status]}`}
        >
          {detailByStatus[state.status]}
        </div>
      )}
    </Card>
  );
};

export default ClientProbeCard;

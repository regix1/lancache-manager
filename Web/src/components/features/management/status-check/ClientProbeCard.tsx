import React, { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { MonitorSmartphone } from 'lucide-react';
import { Card } from '@components/ui/Card';
import { Button } from '@components/ui/Button';
import LoadingSpinner from '@components/common/LoadingSpinner';
import { CLIENT_PROBE_HOST, CLIENT_PROBE_TIMEOUT_MS, CLIENT_PROBE_URL } from './constants';
import type { ClientProbeState, ClientProbeStatus } from './types';

// Terminal fallback for the mixed-content case: an https-served dashboard can never fire a
// plain-HTTP fetch at the cache, but the person reading it can, from any shell on this device.
const BLOCKED_FALLBACK_COMMANDS = [
  `nslookup ${CLIENT_PROBE_HOST}`,
  `curl -I ${CLIENT_PROBE_URL}`
].join('\n');

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
  const [copied, setCopied] = useState(false);

  const handleCopyCommands = async () => {
    try {
      // The blocked state only exists on https pages, i.e. secure contexts, where the
      // Clipboard API is always available.
      await navigator.clipboard.writeText(BLOCKED_FALLBACK_COMMANDS);
      setCopied(true);
      setTimeout(() => setCopied(false), 2000);
    } catch {
      // Clipboard permission denied - the commands stay visible for manual selection.
    }
  };

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
      {state.status === 'blocked' && (
        <div className="mt-3">
          <div className="flex items-center justify-between gap-2 mb-2">
            <p className="text-xs font-medium text-themed-primary">
              {t(`${keys}.probeBlockedFallback`)}
            </p>
            <Button
              size="xs"
              variant={copied ? 'filled' : 'default'}
              color={copied ? 'green' : undefined}
              className="flex-shrink-0"
              onClick={() => void handleCopyCommands()}
            >
              {copied ? t(`${keys}.probeCopied`) : t('common.copy')}
            </Button>
          </div>
          <code className="block whitespace-pre-wrap break-all text-xs font-mono px-3 py-2 rounded-md bg-themed-secondary text-themed-secondary">
            {BLOCKED_FALLBACK_COMMANDS}
          </code>
          <p className="text-xs text-themed-muted mt-2">{t(`${keys}.probeBlockedFallbackHint`)}</p>
        </div>
      )}
    </Card>
  );
};

export default ClientProbeCard;

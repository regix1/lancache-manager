import React from 'react';
import { useTranslation } from 'react-i18next';
import { MonitorSmartphone } from 'lucide-react';
import { Card } from '@components/ui/Card';
import { Button } from '@components/ui/Button';
import { Tooltip } from '@components/ui/Tooltip';
import { HelpPopover, HelpSection } from '@components/ui/HelpPopover';
import LoadingSpinner from '@components/common/LoadingSpinner';
import { useCopyFeedback } from '@hooks/useCopyFeedback';
import { copyText } from '@utils/clipboard';
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
  /** True when the server sweep heartbeat-verified CLIENT_PROBE_HOST this session. Paired with
   *  an inconclusive/unreachable browser probe, this is the evidence that the browser is using a
   *  different DNS resolver than the network's - see the probeResolverMismatch caveat below. */
  hostHeartbeatVerified: boolean;
}

const GLYPH_CLASS_BY_STATUS: Record<ClientProbeStatus, string> = {
  checking: 'status-check-glyph--neutral',
  intercepted: 'status-check-glyph--success',
  inconclusive: 'status-check-glyph--warning',
  unreachable: 'status-check-glyph--error',
  blocked: 'status-check-glyph--info'
};

const ClientProbeCard: React.FC<ClientProbeCardProps> = ({
  state,
  onRetry,
  hostHeartbeatVerified
}) => {
  const { t } = useTranslation();
  const keys = 'management.sections.statusCheck';
  const [copied, markCopied] = useCopyFeedback(false);

  const handleCopyCommands = async () => {
    // The blocked state only exists on https pages, i.e. secure contexts, where the
    // Clipboard API is always available.
    if (await copyText(BLOCKED_FALLBACK_COMMANDS)) {
      markCopied(true);
    }
  };

  const titleByStatus: Record<ClientProbeStatus, string> = {
    checking: t(`${keys}.probeChecking`),
    intercepted: t(`${keys}.probeIntercepted`),
    inconclusive: t(`${keys}.probeInconclusive`),
    unreachable: t(`${keys}.probeUnreachable`),
    blocked: t(`${keys}.probeBlocked`)
  };

  // One short line under the title, only where the title leaves the reader asking why. A device
  // that reached the cache needs no second sentence: who answered sits in the title's tooltip.
  // A blocked probe explains itself in the popover beside its title.
  const detailByStatus: Record<ClientProbeStatus, string> = {
    checking: '',
    intercepted: '',
    inconclusive: t(`${keys}.probeInconclusiveDetail`, { domain: CLIENT_PROBE_HOST }),
    unreachable: t(`${keys}.probeUnreachableDetail`, {
      domain: CLIENT_PROBE_HOST,
      seconds: CLIENT_PROBE_TIMEOUT_MS / 1000
    }),
    blocked: ''
  };

  return (
    <Card>
      <div className="flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
        <div className="flex items-center gap-3 min-w-0">
          <div className={`status-check-glyph ${GLYPH_CLASS_BY_STATUS[state.status]}`}>
            {state.status === 'checking' ? (
              <LoadingSpinner inline size="md" />
            ) : (
              <MonitorSmartphone className="w-5 h-5" />
            )}
          </div>
          <div className="status-check-verdict-copy">
            <div className="status-check-verdict-title status-check-help-line">
              {state.status === 'intercepted' ? (
                <Tooltip
                  content={t(`${keys}.probeInterceptedDetail`, { host: state.servedBy })}
                  className="min-w-0"
                >
                  <p className="status-check-verdict-line">{titleByStatus[state.status]}</p>
                </Tooltip>
              ) : (
                <p className="status-check-verdict-line">{titleByStatus[state.status]}</p>
              )}
              {state.status === 'blocked' && (
                <HelpPopover width={320} aria-label={t(`${keys}.probeBlockedLabel`)}>
                  <HelpSection title={titleByStatus[state.status]} variant="subtle">
                    <p>{t(`${keys}.probeBlockedDetail`)}</p>
                    <p className="mt-2">{t(`${keys}.probeBlockedFallbackHint`)}</p>
                  </HelpSection>
                </HelpPopover>
              )}
            </div>
            {detailByStatus[state.status] && (
              <p className="status-check-verdict-sub">{detailByStatus[state.status]}</p>
            )}
            {hostHeartbeatVerified && state.status === 'unreachable' && (
              <p className="status-check-todo status-check-help-line">
                {t(`${keys}.probeResolverMismatchTodo`)}
                <HelpPopover width={320} aria-label={t(`${keys}.probeResolverMismatchLabel`)}>
                  <HelpSection title={t(`${keys}.probeResolverMismatchShort`)} variant="subtle">
                    <p>{t(`${keys}.probeResolverMismatch`)}</p>
                    <p className="mt-2">
                      {t(`${keys}.probeResolverMismatchHelp`)}{' '}
                      <a
                        href="https://one.one.one.one/help"
                        target="_blank"
                        rel="noreferrer"
                        className="font-medium underline hover:no-underline text-themed-primary focus-ring"
                      >
                        {t(`${keys}.probeResolverMismatchCheck`)}
                      </a>
                    </p>
                  </HelpSection>
                </HelpPopover>
              </p>
            )}
          </div>
        </div>
        {state.status !== 'checking' && state.status !== 'blocked' && (
          <Button variant="subtle" size="sm" className="flex-shrink-0" onClick={onRetry}>
            {t(`${keys}.probeRetry`)}
          </Button>
        )}
      </div>
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
        </div>
      )}
    </Card>
  );
};

export default ClientProbeCard;

import React, { useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Card } from '@components/ui/Card';
import { Button } from '@components/ui/Button';
import { EnhancedDropdown, type DropdownOption } from '@components/ui/EnhancedDropdown';
import ApiService, {
  type StatusCheckDomainGroup,
  type StatusCheckTestDomainResponse
} from '@services/api.service';
import { getErrorMessage } from '@utils/error';
import DomainLeafRow from './DomainLeafRow';
import { formatServiceLabel } from './helpers';

interface TestDomainCardProps {
  groups: StatusCheckDomainGroup[] | null;
}

const TestDomainCard: React.FC<TestDomainCardProps> = ({ groups }) => {
  const { t } = useTranslation();
  const keys = 'management.sections.statusCheck';

  const [domain, setDomain] = useState('');
  const [dropdownValue, setDropdownValue] = useState('');
  const [testing, setTesting] = useState(false);
  const [response, setResponse] = useState<StatusCheckTestDomainResponse | null>(null);
  const [testError, setTestError] = useState<string | null>(null);

  const options = useMemo<DropdownOption[]>(
    () =>
      (groups ?? []).map((group) => ({
        value: group.name,
        label: formatServiceLabel(group.name),
        description: group.description,
        submenuTitle: formatServiceLabel(group.name),
        submenu: group.domains.map((entry) => ({ value: entry, label: entry }))
      })),
    [groups]
  );

  const handleDropdownChange = (value: string) => {
    // Submenu selections arrive as "service:domain"; a bare service value has no domain.
    const separatorIndex = value.indexOf(':');
    if (separatorIndex < 0) return;
    setDropdownValue(value);
    setDomain(value.slice(separatorIndex + 1));
  };

  const handleTest = async () => {
    const trimmed = domain.trim();
    if (!trimmed || testing) return;
    setTesting(true);
    setTestError(null);
    try {
      const result = await ApiService.testStatusCheckDomain(trimmed);
      setResponse(result);
    } catch (error) {
      setTestError(getErrorMessage(error));
    } finally {
      setTesting(false);
    }
  };

  const heartbeat = response?.heartbeat ?? null;

  return (
    <Card>
      <p className="text-xs text-themed-muted mb-3">{t(`${keys}.testLaneDesc`)}</p>
      <div className="flex flex-col gap-2 sm:flex-row sm:items-center">
        <EnhancedDropdown
          options={options}
          value={dropdownValue}
          onChange={handleDropdownChange}
          size="md"
          variant="button"
          customTriggerLabel={t(`${keys}.browseDomains`)}
          triggerAriaLabel={t(`${keys}.browseDomains`)}
          disabled={options.length === 0}
          placeholder={t(`${keys}.domainsUnavailable`)}
          dropdownWidth="320px"
          className="sm:w-56 min-h-10"
        />
        <input
          type="text"
          value={domain}
          onChange={(event: React.ChangeEvent<HTMLInputElement>) => setDomain(event.target.value)}
          onKeyDown={(event: React.KeyboardEvent<HTMLInputElement>) => {
            if (event.key === 'Enter') void handleTest();
          }}
          placeholder={t(`${keys}.domainPlaceholder`)}
          aria-label={t(`${keys}.testLane`)}
          className="themed-input flex-1 min-w-0 h-10 min-h-10 px-3"
        />
        <Button
          variant="filled"
          color="blue"
          size="md"
          className="min-h-10 flex-shrink-0"
          loading={testing}
          disabled={!domain.trim()}
          onClick={() => void handleTest()}
        >
          {t(`${keys}.testButton`)}
        </Button>
      </div>

      {testError && (
        <div className="mt-3 text-xs p-2.5 rounded leading-relaxed bg-[var(--theme-error-bg)] text-[var(--theme-error-text)]">
          {t(`${keys}.testFailed`, { error: testError })}
        </div>
      )}

      {response && (
        <div className="mt-3 pt-3 border-t border-themed-secondary space-y-1">
          <DomainLeafRow result={response.result} />
          {heartbeat && (
            <p className="text-xs text-themed-muted ml-6">
              {heartbeat.reachable
                ? heartbeat.servedBy
                  ? t(`${keys}.testHeartbeatOk`, { host: heartbeat.servedBy })
                  : t(`${keys}.testHeartbeatOkNoHost`)
                : t(`${keys}.testHeartbeatFailed`, {
                    error: heartbeat.error ?? t(`${keys}.unknownError`)
                  })}
            </p>
          )}
        </div>
      )}
    </Card>
  );
};

export default TestDomainCard;

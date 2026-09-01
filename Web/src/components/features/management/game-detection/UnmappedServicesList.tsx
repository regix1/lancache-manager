import React from 'react';
import { useTranslation } from 'react-i18next';
import { ChevronDown } from 'lucide-react';
import { formatBytes, formatCount } from '@utils/formatters';
import { getServiceDisplayName } from '@utils/serviceDisplayName';
import { rowToggleHandlers } from '@utils/rowToggle';
import Badge from '@components/ui/Badge';
import { CollapsibleRegion } from '@components/ui/CollapsibleRegion';
import { EmptyState } from '@components/ui/ManagerCard';
import type { UnmappedService } from '../../../../types';
import CacheEntityList from './CacheEntityList';
import ExpandableList from './ExpandableList';

interface UnmappedServicesListProps {
  services: UnmappedService[];
}

const MAX_INITIAL_URLS = 20;

const filterAndSortServices = (services: UnmappedService[], searchQuery: string) => {
  const query = searchQuery.toLowerCase();
  const filtered = services.filter((service) => service.service.toLowerCase().includes(query));

  filtered.sort((a, b) => a.service.localeCompare(b.service, undefined, { sensitivity: 'base' }));

  return filtered;
};

const UnmappedServicesList: React.FC<UnmappedServicesListProps> = ({ services }) => {
  const { t } = useTranslation();

  if (services.length === 0) {
    return (
      <EmptyState
        title={t('management.gameDetection.emptyState.nothingUnmapped')}
        subtitle={t('management.gameDetection.emptyState.nothingUnmappedSubtitle')}
      />
    );
  }

  return (
    <div className="space-y-3">
      {/* Names the scan these figures came from, so they are not read as the dashboard's
          separately computed unmapped total. */}
      <p className="mgmt-scanmeta">{t('management.gameDetection.unmappedFromScan')}</p>

      <CacheEntityList<UnmappedService>
        items={services}
        searchPlaceholder={t('management.gameDetection.placeholders.searchUnmapped')}
        getEmptyMessage={(query) => t('management.gameDetection.noUnmappedMatching', { query })}
        itemLabel={t('management.gameDetection.unmappedLabel')}
        getItemKey={(service) => service.service}
        filterAndSortItems={filterAndSortServices}
        renderItem={(service, state) => {
          const displayName = getServiceDisplayName(service.service);
          // The scan caps sample URLs at five and sends none for a service it could read no
          // usable key from, which leaves the row with nothing to expand into.
          const hasSampleUrls = service.sample_urls.length > 0;
          const toggleDetails = () => state.onToggleDetails(service.service);

          return (
            <div>
              {/* The row is the only control, so aria-expanded belongs on it rather than on a
                  nested button. Same shape as DatasourceListItem. */}
              <div
                className={`mgmt-row flex-wrap${
                  hasSampleUrls ? ' mgmt-row--interactive focus-ring--inset cursor-pointer' : ''
                }`}
                {...(hasSampleUrls
                  ? { 'aria-expanded': state.isExpanded, ...rowToggleHandlers(toggleDetails) }
                  : {})}
              >
                <div className="mgmt-row__body">
                  <p className="mgmt-row__title mgmt-row__title--service truncate">{displayName}</p>
                </div>
                <div className="mgmt-row__actions flex-wrap justify-end">
                  <Badge variant="neutral" className="badge-count badge-count-warning tabular-nums">
                    {t('management.gameDetection.unmappedFileCount', {
                      count: service.file_count,
                      formattedCount: formatCount(service.file_count)
                    })}
                  </Badge>
                  <Badge variant="neutral" className="badge-count tabular-nums">
                    {formatBytes(service.total_bytes)}
                  </Badge>
                  {/* Held open on a row with nothing to expand so every row's counts end on
                      the same edge. The row carries aria-expanded, so the glyph is decorative. */}
                  <span className="w-4 h-4 flex-shrink-0" aria-hidden="true">
                    {hasSampleUrls && (
                      <ChevronDown
                        className={`w-4 h-4 transition duration-200 ease-out${
                          state.isExpanded
                            ? ' rotate-180 text-themed-accent'
                            : ' rotate-0 text-themed-muted'
                        }`}
                      />
                    )}
                  </span>
                </div>
              </div>
              {hasSampleUrls && (
                <CollapsibleRegion open={state.isExpanded} contentClassName="mgmt-row-detail">
                  <ExpandableList
                    items={service.sample_urls}
                    maxInitial={MAX_INITIAL_URLS}
                    labelKey="management.gameDetection.sampleUrls"
                    showingLabelKey="management.gameDetection.showingUrls"
                  />
                </CollapsibleRegion>
              )}
            </div>
          );
        }}
      />
    </div>
  );
};

export default UnmappedServicesList;

import React, { useState, useMemo, useCallback } from 'react';
import { PieChart, Maximize2, Minimize2 } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { isActiveGame, buildGamesOnDiskDisplayStats, getChartGames } from '@utils/gameDetection';
import { getServiceDisplayName } from '@utils/serviceDisplayName';
import { useGameDetection } from '@contexts/DashboardDataContext/hooks';
import { Card } from '@components/ui/Card';
import { Button } from '@components/ui/Button';
import Badge from '@components/ui/Badge';
import { SegmentedControl } from '@components/ui/SegmentedControl';
import { EnhancedDropdown } from '@components/ui/EnhancedDropdown';
import { useMediaQuery } from '@hooks/useMediaQuery';
import { Tooltip } from '@components/ui/Tooltip';
import { HelpPopover, HelpSection, HelpDefinition } from '@components/ui/HelpPopover';
import { EmptyState } from '@components/ui/ManagerCard';
import LoadingSpinner from '@components/common/LoadingSpinner';
import DoughnutChart from './DoughnutChart';
import ChartLegend from './ChartLegend';
import CompareLineChart from './CompareLineChart';
import { useChartData } from './useChartData';
import { getInsightCards, getLegendColorClass, type FooterStats } from './serviceLegendClasses';
import { formatBytes } from '@utils/formatters';
import type { ServiceAnalyticsChartProps, TabId, LegendItem } from './types';
import { TAB_DESCRIPTION_KEYS, ALL_GAME_SERVICES, DEFAULT_GAME_SERVICE } from './constants';
import { APP_EVENTS } from '@utils/constants';

interface TabOption {
  value: TabId;
  label: string;
  tooltip?: string;
}

const ServiceAnalyticsChart: React.FC<ServiceAnalyticsChartProps> = React.memo(
  ({ serviceStats, glassmorphism = false, loading = false, onExpandedChange }) => {
    const { t } = useTranslation();
    const [activeTab, setActiveTab] = useState<TabId>('service');
    const [showList, setShowList] = useState<boolean>(true);
    const [gameService, setGameService] = useState<string>(ALL_GAME_SERVICES);
    const { gameDetectionData } = useGameDetection();
    const isCompareTab = activeTab === 'hit-ratio';
    const hasBreakdownList = !isCompareTab;
    // Five labelled tabs do not fit a phone-width panel, so the view picker becomes a dropdown
    // below the same breakpoint the stylesheet uses. Swapped in JS rather than rendered twice
    // and hidden, so only one picker exists in the accessibility tree. [31]
    const isPhone = useMediaQuery('(max-width: 639.98px)');

    // Call onExpandedChange initially and when showList changes
    React.useEffect(() => {
      onExpandedChange?.(hasBreakdownList ? showList : true);
    }, [hasBreakdownList, showList, onExpandedChange]);

    const handleToggleList = useCallback(() => {
      setShowList((prev) => !prev);
    }, []);

    const games = useMemo(() => getChartGames(gameDetectionData), [gameDetectionData]);

    const gamesOnDisk = useMemo(
      () => buildGamesOnDiskDisplayStats(gameDetectionData),
      [gameDetectionData]
    );

    // Only services that actually have games on disk are offered, ordered by how much disk they
    // hold, so the picker never lists a service that would render an empty chart.
    const gameServiceOptions = useMemo(() => {
      const bytesByService = new Map<string, number>();
      for (const game of games) {
        if (!isActiveGame(game)) continue;
        const service = game.service ?? DEFAULT_GAME_SERVICE;
        bytesByService.set(service, (bytesByService.get(service) ?? 0) + game.total_size_bytes);
      }

      return [
        {
          value: ALL_GAME_SERVICES,
          label: t('dashboard.serviceAnalytics.gameService.all', 'All services')
        },
        ...[...bytesByService.entries()]
          .sort(([, aBytes], [, bBytes]) => bBytes - aBytes)
          .map(([service]) => ({ value: service, label: getServiceDisplayName(service) }))
      ];
    }, [games, t]);

    const filteredGames = useMemo(
      () =>
        gameService === ALL_GAME_SERVICES
          ? games
          : games.filter((game) => (game.service ?? DEFAULT_GAME_SERVICE) === gameService),
      [games, gameService]
    );

    // A rescan can drop the selected service entirely (its last game evicted). Falling back keeps
    // the view from showing an empty chart for a service the picker no longer offers.
    React.useEffect(() => {
      if (gameService === ALL_GAME_SERVICES) return;
      if (!gameServiceOptions.some((option) => option.value === gameService)) {
        setGameService(ALL_GAME_SERVICES);
      }
    }, [gameService, gameServiceOptions]);

    const activeServiceCount = useMemo(
      () => serviceStats.filter((service) => service.totalBytes > 0).length,
      [serviceStats]
    );

    const tabs: TabOption[] = useMemo(
      () => [
        {
          value: 'service',
          label: t('dashboard.serviceAnalytics.tabs.service'),
          tooltip: t('dashboard.serviceAnalytics.tabs.serviceDistribution')
        },
        {
          value: 'hit-ratio',
          label: t('dashboard.serviceAnalytics.tabs.hitRatio'),
          tooltip: t('dashboard.serviceAnalytics.tabs.hitRatioFull')
        },
        {
          value: 'bandwidth',
          label: t('dashboard.serviceAnalytics.tabs.bandwidth'),
          tooltip: t('dashboard.serviceAnalytics.tabs.bandwidthFull')
        },
        {
          value: 'misses',
          label: t('dashboard.serviceAnalytics.tabs.misses'),
          tooltip: t('dashboard.serviceAnalytics.tabs.missesFull')
        },
        {
          value: 'games',
          label: t('dashboard.serviceAnalytics.tabs.games', 'Games'),
          tooltip: t('dashboard.serviceAnalytics.tabs.gamesFull', 'Games on Disk')
        }
      ],
      [t]
    );

    const activeTabConfig = useMemo(
      () => tabs.find((tab) => tab.value === activeTab) ?? tabs[0],
      [tabs, activeTab]
    );

    // Get chart data from hook
    const chartData = useChartData(serviceStats, activeTab, filteredGames);

    // Transform to legend items
    const legendItems: LegendItem[] = useMemo(() => {
      if (chartData.isEmpty || !chartData.datasets[0]) return [];

      const dataset = chartData.datasets[0];
      const originalData = dataset.originalData ?? dataset.data;
      return chartData.labels.map((label, index) => ({
        label,
        value: originalData[index],
        color: dataset.backgroundColor[index],
        percentage: chartData.total > 0 ? (originalData[index] / chartData.total) * 100 : 0,
        valueLabel: formatBytes(originalData[index]),
        colorClassName: getLegendColorClass(label, index, activeTab)
      }));
    }, [activeTab, chartData]);

    // Center label based on active tab
    const centerLabel = useMemo(() => {
      switch (activeTab) {
        case 'bandwidth':
          return t('dashboard.serviceAnalytics.centerLabels.saved', 'Cache Hits');
        case 'misses':
          return t('dashboard.serviceAnalytics.centerLabels.internet', 'Cache Misses');
        case 'games':
          return t('dashboard.serviceAnalytics.centerLabels.onDisk', 'On Disk');
        case 'hit-ratio':
          return t('dashboard.serviceAnalytics.centerLabels.total');
        default:
          return t('dashboard.serviceAnalytics.centerLabels.total');
      }
    }, [activeTab, t]);

    // Stats for footer
    const footerStats: FooterStats = useMemo(() => {
      if (activeTab === 'games') {
        const activeGames = filteredGames.filter(isActiveGame);
        // The cached totals cover every service, so they only answer the unfiltered question.
        // Once a service is picked the totals have to come from the games actually shown, or the
        // readout would report the whole disk against one service's slices.
        const isServiceFiltered = gameService !== ALL_GAME_SERVICES;
        const totalDisk = isServiceFiltered
          ? activeGames.reduce((sum, game) => sum + game.total_size_bytes, 0)
          : (gamesOnDisk?.totalSize ?? gameDetectionData?.games_on_disk_bytes ?? 0);
        const sorted = [...activeGames].sort((a, b) => b.total_size_bytes - a.total_size_bytes);
        const largest = sorted[0];
        return {
          totalBytes: totalDisk,
          hitRatio: 0,
          missBytes: 0,
          serviceCount: 0,
          gameCount: isServiceFiltered
            ? activeGames.length
            : (gamesOnDisk?.gameCount ??
              gameDetectionData?.games_on_disk_count ??
              activeGames.length),
          largestGame: largest?.game_name ?? '',
          largestGameBytes: largest?.total_size_bytes ?? 0,
          topServiceName: '',
          topServiceBytes: 0,
          totalHitBytes: 0
        };
      }
      const totalBytes = serviceStats.reduce((sum, s) => sum + s.totalBytes, 0);
      const totalHits = serviceStats.reduce((sum, s) => sum + s.totalCacheHitBytes, 0);
      const totalMisses = serviceStats.reduce((sum, s) => sum + s.totalCacheMissBytes, 0);
      const hitRatio = totalBytes > 0 ? (totalHits / totalBytes) * 100 : 0;
      const activeServices = serviceStats.filter((service) => service.totalBytes > 0);
      const sortedByTotal = [...activeServices].sort((a, b) => b.totalBytes - a.totalBytes);
      const top = sortedByTotal[0];
      return {
        totalBytes,
        hitRatio,
        missBytes: totalMisses,
        serviceCount: activeServiceCount,
        gameCount: 0,
        largestGame: '',
        largestGameBytes: 0,
        topServiceName: top?.service ?? '',
        topServiceBytes: top?.totalBytes ?? 0,
        totalHitBytes: totalHits
      };
    }, [
      serviceStats,
      activeTab,
      filteredGames,
      gameService,
      gameDetectionData,
      gamesOnDisk,
      activeServiceCount
    ]);

    const insightCards = useMemo(
      () => getInsightCards(activeTab, footerStats, chartData, t),
      [activeTab, footerStats, chartData, t]
    );

    const hideListLabel = t('dashboard.serviceAnalytics.hideList', 'Hide breakdown');
    const showListLabel = t('dashboard.serviceAnalytics.showList', 'Show breakdown');
    const toggleAriaLabel = showList ? hideListLabel : showListLabel;

    return (
      <Card glassmorphism={glassmorphism} className="service-chart-panel">
        {/* Header */}
        <div className="service-analytics-header">
          <div className="service-analytics-heading">
            <h3 className="dash-panel-title">{t('dashboard.serviceAnalytics.title')}</h3>
            <HelpPopover width={320}>
              <HelpSection title={t('dashboard.serviceAnalytics.help.aboutTitle')}>
                {t('dashboard.serviceAnalytics.help.about')}
              </HelpSection>
              <HelpSection title={t('dashboard.serviceAnalytics.help.viewsTitle')} variant="subtle">
                <HelpDefinition
                  items={tabs.map((tab) => ({
                    term: tab.label,
                    description: t(
                      `dashboard.serviceAnalytics.descriptions.${TAB_DESCRIPTION_KEYS[tab.value]}`
                    )
                  }))}
                />
              </HelpSection>
            </HelpPopover>
          </div>

          {/* View picker first, then the actions. The Compare view has no breakdown list, so its
              toggle is not rendered at all, and the tab strip keeps the same left edge on all five
              views. [27] The toggle carries the auto margin, so it and the service picker sit
              against the right edge once the header stacks and the row has room to spare. */}
          <div className="service-analytics-controls">
            {isPhone ? (
              <EnhancedDropdown
                options={tabs}
                value={activeTab}
                onChange={(next: string) => setActiveTab(next as TabId)}
                size="md"
                variant="button"
                className="service-analytics-view-select"
              />
            ) : (
              <SegmentedControl
                options={tabs}
                value={activeTab}
                onChange={(next) => setActiveTab(next as TabId)}
                size="md"
                showLabels
              />
            )}
            {/* Toggle and service picker travel together as one right-aligned group, so when the
                row runs out of width they wrap as a pair and stay against the right edge instead
                of the picker dropping to its own left-aligned line. */}
            <div className="service-analytics-actions">
              {hasBreakdownList && (
                <Tooltip content={toggleAriaLabel}>
                  <Button
                    variant="filled"
                    color="gray"
                    size="md"
                    onClick={handleToggleList}
                    aria-pressed={!showList}
                    aria-label={toggleAriaLabel}
                    className="service-analytics-toggle btn-icon-square"
                  >
                    {showList ? (
                      <Minimize2 className="w-4 h-4" />
                    ) : (
                      <Maximize2 className="w-4 h-4" />
                    )}
                  </Button>
                </Tooltip>
              )}
              {/* Sits after the toggle so the trigger's width can only ever grow rightwards into
                  empty space. With it before the toggle, a longer service label shoved the button
                  sideways every time the selection changed.
                  Shown whenever the view has any service at all: an earlier version required two
                  before appearing, which just made the control vanish on setups where detection
                  put every game under one service. */}
              {activeTab === 'games' && gameServiceOptions.length > 1 && (
                <EnhancedDropdown
                  options={gameServiceOptions}
                  value={gameService}
                  onChange={setGameService}
                  size="md"
                  variant="button"
                  prefix={t('dashboard.serviceAnalytics.gameService.prefix', 'Service:')}
                  className="service-analytics-game-service-select"
                />
              )}
            </div>
          </div>
        </div>

        {loading ? (
          <div className="service-analytics-loading">
            <LoadingSpinner size="lg" />
          </div>
        ) : !chartData.isEmpty ? (
          <>
            {/* Main content - side by side */}
            <div
              className="service-analytics-body"
              data-chart-mode={activeTab}
              data-show-list={hasBreakdownList && showList}
            >
              {isCompareTab ? (
                <div className="analytics-compare-container">
                  <CompareLineChart serviceStats={serviceStats} />
                </div>
              ) : (
                <>
                  {/* Chart */}
                  <div className="analytics-chart-container">
                    <DoughnutChart
                      labels={chartData.labels}
                      datasets={chartData.datasets}
                      total={chartData.total}
                      centerLabel={centerLabel}
                      gameSliceExtras={chartData.gameSliceExtras}
                      ariaLabel={t('dashboard.serviceAnalytics.tabs.serviceDistribution')}
                    />
                  </div>

                  {/* Legend with progress bars */}
                  {showList && (
                    <div className="well-surface analytics-list-container">
                      <div className="analytics-list-header">
                        <span>{activeTabConfig.tooltip ?? activeTabConfig.label}</span>
                        <Badge
                          variant="neutral"
                          className="badge-count"
                          ariaLabel={t('dashboard.serviceAnalytics.itemCount', {
                            count: legendItems.length,
                            defaultValue: '{{count}} items'
                          })}
                        >
                          {legendItems.length}
                        </Badge>
                      </div>
                      <ChartLegend items={legendItems} />
                    </div>
                  )}
                </>
              )}
            </div>

            {/* Stats footer — shared readout family so the border line matches the
                Downloads panel's footer across the row */}
            <div className="dash-readout dash-readout--footer">
              {insightCards.map((stat) => (
                <div key={stat.label} className="dash-readout-item">
                  <div
                    className={`dash-readout-value${stat.tone === 'primary' ? ' is-primary' : ''}`}
                  >
                    {stat.value}
                  </div>
                  <div className="caps-label caps-label--wide dash-readout-label">{stat.label}</div>
                </div>
              ))}
            </div>
          </>
        ) : (
          <div className="well-surface dash-well p-3 flex flex-1">
            <EmptyState
              variant="panel"
              icon={PieChart}
              title={t('dashboard.serviceAnalytics.empty.title')}
              subtitle={t('dashboard.serviceAnalytics.empty.description')}
              action={
                <Button
                  variant="filled"
                  color="gray"
                  size="sm"
                  onClick={() =>
                    window.dispatchEvent(
                      new CustomEvent(APP_EVENTS.NAVIGATE_TO_TAB, { detail: { tab: 'downloads' } })
                    )
                  }
                >
                  {t('dashboard.serviceAnalytics.empty.action', 'View Logs')}
                </Button>
              }
            />
          </div>
        )}
      </Card>
    );
  }
);

ServiceAnalyticsChart.displayName = 'ServiceAnalyticsChart';

export default ServiceAnalyticsChart;

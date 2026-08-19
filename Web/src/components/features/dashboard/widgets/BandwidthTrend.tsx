import React, { memo, useEffect, useMemo, useState } from 'react';
import { Line } from 'react-chartjs-2';
import {
  CategoryScale,
  Chart as ChartJS,
  Filler,
  Legend,
  LinearScale,
  LineElement,
  PointElement,
  Tooltip,
  type ChartData,
  type ChartOptions
} from 'chart.js';
import { Activity } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { useSparklines } from '@contexts/DashboardDataContext/hooks';
import { useReaderClock } from '@hooks/useReaderClock';
import Badge from '@components/ui/Badge';
import LoadingSpinner from '@components/common/LoadingSpinner';
import { EmptyState } from '@components/ui/ManagerCard';
import { HelpNote, HelpPopover, HelpSection } from '@components/ui/HelpPopover';
import { SegmentedControl } from '@components/ui/SegmentedControl';
import { getThemeColor, useThemeRevision } from '../ServiceAnalyticsChart/chartTheme';
import {
  bandwidthTickLabel,
  hasBandwidthPoints,
  lineChartScales,
  useHiddenSeries
} from './bandwidthChart';
import EventCompareChart from './EventCompareChart';
import LineChartLegend from './LineChartLegend';
import { hideLineChartTooltip, lineChartTooltip } from './lineChartTooltip';

ChartJS.register(CategoryScale, LinearScale, PointElement, LineElement, Filler, Tooltip, Legend);

const EMPTY_POINTS: number[] = [];

type ChartTab = 'bandwidth' | 'compare';

const BandwidthTrend: React.FC = memo(() => {
  const { t } = useTranslation();
  const clock = useReaderClock();
  const themeRevision = useThemeRevision();
  const { sparklines, loading } = useSparklines();
  const [chartTab, setChartTab] = useState<ChartTab>('bandwidth');
  const { hiddenSeries, toggleSeries } = useHiddenSeries();

  const bucketMinutes = sparklines?.bucketMinutes ?? 1440;
  const starts = sparklines?.bucketStarts ?? EMPTY_POINTS;
  const saved = sparklines?.bandwidthSaved?.data ?? EMPTY_POINTS;
  const served = sparklines?.totalServed?.data ?? EMPTY_POINTS;
  const pointCount = Math.min(starts.length, saved.length, served.length);
  const hasSeries = pointCount > 0 && hasBandwidthPoints(saved, served);
  const isCompare = chartTab === 'compare';

  useEffect(() => hideLineChartTooltip, [hasSeries, isCompare]);

  const chartData: ChartData<'line'> = useMemo(() => {
    void themeRevision;
    const labels = starts
      .slice(0, pointCount)
      .map((start) => bandwidthTickLabel(start, bucketMinutes, clock));
    return {
      labels,
      datasets: [
        {
          label: t('widgets.bandwidthTrend.served'),
          data: served.slice(0, pointCount),
          borderColor: getThemeColor('--theme-primary'),
          backgroundColor: getThemeColor('--theme-primary-subtle'),
          fill: true,
          tension: 0.25,
          hidden: hiddenSeries.has(0),
          pointRadius: 0,
          pointHoverRadius: 4
        },
        {
          label: t('widgets.bandwidthTrend.saved'),
          data: saved.slice(0, pointCount),
          borderColor: getThemeColor('--theme-success'),
          backgroundColor: getThemeColor('--theme-success-muted'),
          fill: true,
          tension: 0.25,
          hidden: hiddenSeries.has(1),
          pointRadius: 0,
          pointHoverRadius: 4
        }
      ]
    };
  }, [bucketMinutes, clock, hiddenSeries, pointCount, saved, served, starts, t, themeRevision]);

  const chartOptions: ChartOptions<'line'> = useMemo(() => {
    void themeRevision;
    return {
      responsive: true,
      maintainAspectRatio: false,
      layout: {
        padding: { top: 4, right: 16, bottom: 4, left: 4 }
      },
      interaction: { mode: 'index', intersect: false },
      plugins: {
        legend: {
          display: false
        },
        tooltip: lineChartTooltip({
          swatchClass: (datasetIndex) =>
            datasetIndex === 1 ? 'line-trend-swatch-success' : 'line-trend-swatch-primary'
        })
      },
      scales: lineChartScales()
    };
  }, [themeRevision]);

  const legendItems = useMemo(
    () => [
      {
        label: t('widgets.bandwidthTrend.served'),
        colorClass: 'line-trend-swatch-primary',
        hidden: hiddenSeries.has(0)
      },
      {
        label: t('widgets.bandwidthTrend.saved'),
        colorClass: 'line-trend-swatch-success',
        hidden: hiddenSeries.has(1)
      }
    ],
    [hiddenSeries, t]
  );

  // A fresh element every render defeats the memo on EventCompareChart, which re-renders it on
  // every sparkline refresh. [37]
  const tabControl = useMemo(
    () => (
      <SegmentedControl
        size="md"
        showLabels
        value={chartTab}
        onChange={(value) => setChartTab(value === 'compare' ? 'compare' : 'bandwidth')}
        options={[
          { value: 'bandwidth', label: t('widgets.bandwidthTrend.title') },
          { value: 'compare', label: t('widgets.eventCompare.title') }
        ]}
      />
    ),
    [chartTab, t]
  );

  return (
    <div className="widget-card widget-card--wide line-trend-card">
      <div className="line-trend-header">
        <div className="line-trend-heading flex items-center gap-1 min-h-6">
          <h3 className="dash-panel-title">
            {t(isCompare ? 'widgets.eventCompare.title' : 'widgets.bandwidthTrend.title')}
          </h3>
          <HelpPopover width={280}>
            {isCompare ? (
              <>
                <HelpSection title={t('widgets.eventCompare.help.aboutTitle')}>
                  {t('widgets.eventCompare.help.about')}
                </HelpSection>
                <HelpNote type="info">{t('widgets.eventCompare.help.axis')}</HelpNote>
              </>
            ) : (
              <>
                <HelpSection title={t('widgets.bandwidthTrend.help.aboutTitle')}>
                  {t('widgets.bandwidthTrend.help.about')}
                </HelpSection>
                <HelpNote type="info">{t('widgets.bandwidthTrend.help.resolution')}</HelpNote>
              </>
            )}
          </HelpPopover>
          {hasSeries ? (
            <Badge variant="neutral">
              {t(`widgets.bandwidthTrend.resolution.${bucketMinutes}`)}
            </Badge>
          ) : null}
        </div>
        {!isCompare ? <div className="line-trend-controls">{tabControl}</div> : null}
      </div>

      {isCompare ? (
        <EventCompareChart tabControl={tabControl} />
      ) : (
        <div className="well-surface dash-line-chart-well">
          {loading && !hasSeries ? (
            <div className="dash-line-chart-placeholder">
              <LoadingSpinner size="sm" inline />
              <span>{t('common.loading')}</span>
            </div>
          ) : hasSeries ? (
            <>
              <LineChartLegend items={legendItems} onToggle={toggleSeries} />
              <div className="dash-line-chart">
                <Line data={chartData} options={chartOptions} />
              </div>
            </>
          ) : (
            <div className="dash-line-chart-placeholder">
              <EmptyState
                icon={Activity}
                subtitle={t('widgets.bandwidthTrend.noDataDesc')}
                title={t('widgets.bandwidthTrend.noDataTitle')}
                variant="panel"
              />
            </div>
          )}
        </div>
      )}
    </div>
  );
});

BandwidthTrend.displayName = 'BandwidthTrend';

export default BandwidthTrend;

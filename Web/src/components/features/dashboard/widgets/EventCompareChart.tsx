import React, { memo, useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { Line } from 'react-chartjs-2';
import {
  CategoryScale,
  Chart as ChartJS,
  Legend,
  LinearScale,
  LineElement,
  PointElement,
  Tooltip,
  type Chart,
  type ChartData,
  type ChartOptions
} from 'chart.js';
import { GitCompare } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { useEvents } from '@contexts/useEvents';
import { useMockMode } from '@contexts/useMockMode';
import { useTimeFilter } from '@contexts/useTimeFilter';
import { useErrorHandler } from '@hooks/useErrorHandler';
import LoadingSpinner from '@components/common/LoadingSpinner';
import { EmptyState } from '@components/ui/ManagerCard';
import { MultiSelectDropdown } from '@components/ui/MultiSelectDropdown';
import { SegmentedControl } from '@components/ui/SegmentedControl';
import ApiService from '@services/api.service';
import MockDataService from '@/test/mockData.service';
import { formatBytes } from '@utils/formatters';
import { getThemeColor, useThemeRevision } from '../ServiceAnalyticsChart/chartTheme';
import LineChartLegend from './LineChartLegend';
import { storage } from '@utils/storage';
import { STORAGE_KEYS } from '@utils/constants';
import { isAbortError } from '@utils/error';
import type { EventCompareResponse } from '@/types';
import {
  defaultCompareEventIds,
  elapsedLabel,
  MAX_COMPARE_EVENTS,
  clipCompareToHours,
  readCompareEventIds
} from './eventCompare';

ChartJS.register(CategoryScale, LinearScale, PointElement, LineElement, Tooltip, Legend);

const EventCompareChart: React.FC<{ tabControl: React.ReactNode }> = memo(({ tabControl }) => {
  const { t } = useTranslation();
  const themeRevision = useThemeRevision();
  const { events } = useEvents();
  const { mockMode } = useMockMode();
  const { getTimeRangeInHours } = useTimeFilter();
  const { notifyError } = useErrorHandler();
  const knownIds = useMemo(() => events.map((event) => event.id), [events]);
  const [selectedIds, setSelectedIds] = useState<number[]>(() => {
    const stored = readCompareEventIds(
      storage.getItem(STORAGE_KEYS.EVENT_COMPARE),
      events.map((event) => event.id)
    );
    return stored.length > 0 ? stored : defaultCompareEventIds(events);
  });
  const [metric, setMetric] = useState<'served' | 'saved'>('served');
  const [compare, setCompare] = useState<EventCompareResponse | null>(null);
  const [loading, setLoading] = useState(false);
  const [hiddenSeries, setHiddenSeries] = useState<ReadonlySet<number>>(() => new Set());
  const chartRef = useRef<Chart<'line'> | null>(null);

  useEffect(() => {
    setSelectedIds((current) => {
      const kept = current.filter((id) => knownIds.includes(id));
      if (kept.length > 0) {
        return kept;
      }
      return defaultCompareEventIds(events);
    });
  }, [events, knownIds]);

  useEffect(() => {
    storage.setItem(STORAGE_KEYS.EVENT_COMPARE, JSON.stringify(selectedIds));
  }, [selectedIds]);

  useEffect(() => {
    if (selectedIds.length === 0) {
      setCompare(null);
      setLoading(false);
      return;
    }

    if (mockMode) {
      setCompare(MockDataService.generateMockEventCompare(selectedIds));
      setLoading(false);
      return;
    }

    const controller = new AbortController();
    setLoading(true);
    ApiService.getEventCompare(selectedIds, controller.signal)
      .then((response) => {
        setCompare(response);
        setLoading(false);
      })
      .catch((error: unknown) => {
        if (isAbortError(error)) {
          return;
        }
        notifyError(t('widgets.eventCompare.errors.load'), error, {
          logLabel: 'Failed to load event compare'
        });
        setCompare(null);
        setLoading(false);
      });

    return () => controller.abort();
  }, [mockMode, notifyError, selectedIds, t]);

  const visibleCompare = useMemo(
    () => (compare ? clipCompareToHours(compare, getTimeRangeInHours()) : null),
    [compare, getTimeRangeInHours]
  );

  const handleSelection = useCallback((values: string[]) => {
    setSelectedIds(values.map((value) => Number(value)).filter((id) => Number.isInteger(id)));
  }, []);

  const options = useMemo(
    () =>
      events.map((event) => ({
        value: String(event.id),
        label: event.name
      })),
    [events]
  );

  const hasSeries = (visibleCompare?.series.length ?? 0) > 0;

  const chartData: ChartData<'line'> = useMemo(() => {
    void themeRevision;
    const labels = (visibleCompare?.elapsedMinutes ?? []).map((minutes) => elapsedLabel(minutes));
    return {
      labels,
      datasets: (visibleCompare?.series ?? []).map((series) => {
        const color = getThemeColor(`--theme-event-${Math.max(1, Math.min(8, series.colorIndex))}`);
        return {
          label: series.name,
          data: metric === 'saved' ? series.saved : series.served,
          borderColor: color,
          backgroundColor: color,
          fill: false,
          tension: 0.25,
          spanGaps: false,
          pointRadius: 0,
          pointHoverRadius: 6,
          pointHitRadius: 12
        };
      })
    };
  }, [visibleCompare, metric, themeRevision]);

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
        tooltip: {
          callbacks: {
            title: (items) => {
              const minutes = visibleCompare?.elapsedMinutes[items[0]?.dataIndex ?? 0] ?? 0;
              return t('widgets.eventCompare.elapsed', { time: elapsedLabel(minutes) });
            },
            label: (item) => {
              const value = typeof item.parsed.y === 'number' ? item.parsed.y : 0;
              return `${item.dataset.label}: ${formatBytes(value)}`;
            }
          }
        }
      },
      scales: {
        x: {
          ticks: {
            color: getThemeColor('--theme-text-muted'),
            maxRotation: 0,
            autoSkip: true,
            maxTicksLimit: 8
          },
          grid: { display: false }
        },
        y: {
          beginAtZero: true,
          grace: '10%',
          ticks: {
            color: getThemeColor('--theme-text-muted'),
            callback: (value) => formatBytes(typeof value === 'number' ? value : Number(value))
          },
          grid: { color: getThemeColor('--theme-border-secondary') }
        }
      }
    };
  }, [visibleCompare, t, themeRevision]);

  const seriesKey = (visibleCompare?.series ?? []).map((series) => series.name).join('|');

  useEffect(() => {
    setHiddenSeries(new Set());
  }, [seriesKey]);

  const legendItems = useMemo(
    () =>
      (visibleCompare?.series ?? []).map((series, index) => ({
        label: series.name,
        colorClass: `line-trend-swatch-event-${Math.max(1, Math.min(8, series.colorIndex))}`,
        hidden: hiddenSeries.has(index)
      })),
    [hiddenSeries, visibleCompare]
  );

  const toggleSeries = useCallback((index: number) => {
    const chart = chartRef.current;
    if (chart) {
      chart.setDatasetVisibility(index, !chart.isDatasetVisible(index));
      chart.update();
    }
    setHiddenSeries((current) => {
      const next = new Set(current);
      if (next.has(index)) {
        next.delete(index);
      } else {
        next.add(index);
      }
      return next;
    });
  }, []);

  return (
    <>
      <div className="line-trend-controls">
        {tabControl}
        <div className="line-trend-actions">
          <MultiSelectDropdown
            options={options}
            values={selectedIds.map(String)}
            onChange={handleSelection}
            placeholder={t('widgets.eventCompare.pickEvents')}
            title={t('widgets.eventCompare.pickEvents')}
            maxSelections={MAX_COMPARE_EVENTS}
            searchable={events.length > 8}
            compactMode
            dropdownWidth="w-72"
            alignRight
          />
          <SegmentedControl
            size="md"
            showLabels
            value={metric}
            onChange={(value) => setMetric(value === 'saved' ? 'saved' : 'served')}
            options={[
              { value: 'served', label: t('widgets.eventCompare.metrics.served') },
              { value: 'saved', label: t('widgets.eventCompare.metrics.saved') }
            ]}
          />
        </div>
      </div>

      <div className="well-surface dash-well dash-line-chart-well">
        {events.length === 0 ? (
          <div className="dash-line-chart-placeholder">
            <EmptyState
              icon={GitCompare}
              subtitle={t('widgets.eventCompare.noEventsDesc')}
              title={t('widgets.eventCompare.noEventsTitle')}
              variant="panel"
            />
          </div>
        ) : loading && !hasSeries ? (
          <div className="dash-line-chart-placeholder">
            <LoadingSpinner size="sm" inline />
            <span>{t('common.loading')}</span>
          </div>
        ) : hasSeries ? (
          <>
            <LineChartLegend items={legendItems} onToggle={toggleSeries} />
            <div className="dash-line-chart">
              <Line ref={chartRef} data={chartData} options={chartOptions} />
            </div>
          </>
        ) : (
          <div className="dash-line-chart-placeholder">
            <EmptyState
              icon={GitCompare}
              subtitle={t('widgets.eventCompare.noDataDesc')}
              title={t('widgets.eventCompare.noDataTitle')}
              variant="panel"
            />
          </div>
        )}
      </div>
    </>
  );
});

EventCompareChart.displayName = 'EventCompareChart';

export default EventCompareChart;

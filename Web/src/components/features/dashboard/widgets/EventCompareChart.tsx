import React, { memo, useCallback, useEffect, useMemo, useState } from 'react';
import { Line } from 'react-chartjs-2';
import {
  CategoryScale,
  Chart as ChartJS,
  Legend,
  LinearScale,
  LineElement,
  PointElement,
  Tooltip,
  type ChartData,
  type ChartOptions
} from 'chart.js';
import { GitCompare } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { useEvents } from '@contexts/useEvents';
import { useMockMode } from '@contexts/useMockMode';
import { useSignalR } from '@contexts/SignalRContext/useSignalR';
import { useTimeFilter } from '@contexts/useTimeFilter';
import { useErrorHandler } from '@hooks/useErrorHandler';
import { useReconnectRefetch } from '@hooks/useReconnectRefetch';
import { useTimeoutCallback } from '@hooks/useTimeoutCallback';
import LoadingSpinner from '@components/common/LoadingSpinner';
import { EmptyState } from '@components/ui/ManagerCard';
import { MultiSelectDropdown } from '@components/ui/MultiSelectDropdown';
import { SegmentedControl } from '@components/ui/SegmentedControl';
import ApiService from '@services/api.service';
import MockDataService from '@/test/mockData.service';
import { getThemeColor, useThemeRevision } from '../ServiceAnalyticsChart/chartTheme';
import { lineChartScales, useHiddenSeries } from './bandwidthChart';
import LineChartLegend from './LineChartLegend';
import { hideLineChartTooltip, lineChartTooltip } from './lineChartTooltip';
import { clampEventColorIndex } from '@utils/eventColors';
import { storage } from '@utils/storage';
import { STORAGE_KEYS } from '@utils/constants';
import { pruneMissingEventIds } from '@contexts/TimeFilterContext.utils';
import { isAbortError } from '@utils/error';
import type { SignalREventName } from '@contexts/SignalRContext/types';
import type { EventCompareResponse } from '@/types';
import {
  defaultCompareEventIds,
  elapsedLabel,
  MAX_COMPARE_EVENTS,
  clipCompareToHours,
  readCompareEventIds,
  repeatedColorPositions
} from './eventCompare';

ChartJS.register(CategoryScale, LinearScale, PointElement, LineElement, Tooltip, Legend);

// The names match the fields on EventCompareSeries, so the picked metric indexes the series.
type CompareMetric = 'served' | 'saved' | 'missed';

// On/off run for a series whose color repeats an earlier one. Long enough to read as a dash at
// this chart's line width, short enough that a brief event still shows more than one segment.
const EVENT_REPEAT_DASH = [6, 4];

// A log processing run raises several of these within a moment of each other, so the burst
// collapses into one fetch of the aggregates.
const REFRESH_DEBOUNCE_MS = 1000;

// The first four redefine what is being compared; the last two move a running event's totals. The
// aggregates come from their own endpoint, so nothing else on the dashboard refreshes them.
const EVENT_COMPARE_EVENTS: readonly SignalREventName[] = [
  'EventCreated',
  'EventUpdated',
  'EventDeleted',
  'EventsCleared',
  'DownloadsRefresh',
  'LogProcessingComplete'
];

const EventCompareChart: React.FC<{ tabControl: React.ReactNode }> = memo(({ tabControl }) => {
  const { t } = useTranslation();
  const themeRevision = useThemeRevision();
  const { events } = useEvents();
  const { mockMode } = useMockMode();
  const { getTimeRangeInHours } = useTimeFilter();
  const { notifyError } = useErrorHandler();
  const { on, off, isConnected } = useSignalR();
  const scheduleReload = useTimeoutCallback(REFRESH_DEBOUNCE_MS);
  const knownIds = useMemo(() => events.map((event) => event.id), [events]);
  const [selectedIds, setSelectedIds] = useState<number[]>(() => {
    const stored = readCompareEventIds(
      storage.getJSON<number[]>(STORAGE_KEYS.EVENT_COMPARE),
      knownIds
    );
    return stored.length > 0 ? stored : defaultCompareEventIds(events);
  });
  const [metric, setMetric] = useState<CompareMetric>('served');
  const [compare, setCompare] = useState<EventCompareResponse | null>(null);
  const [loading, setLoading] = useState(false);
  // Bumped to ask for the same comparison again, the way useRetroDownloads re-asks for its page.
  // The previous chart stays on screen while the fetch runs.
  const [refreshVersion, setRefreshVersion] = useState(0);
  const { hiddenSeries, toggleSeries, seriesKey } = useHiddenSeries();

  // Stable, so the subscription below re-runs only when the connection's own callbacks change.
  const reload = useCallback(() => {
    setRefreshVersion((version) => version + 1);
  }, []);

  // Events raised while the socket was down are never delivered, so a genuine reconnect refetches
  // rather than leaving the figures the chart was drawing when the connection dropped.
  useReconnectRefetch(isConnected, reload);

  useEffect(() => {
    const handleCompareChanged = () => {
      scheduleReload(reload);
    };

    EVENT_COMPARE_EVENTS.forEach((eventName) => on(eventName, handleCompareChanged));

    return () => {
      EVENT_COMPARE_EVENTS.forEach((eventName) => off(eventName, handleCompareChanged));
    };
  }, [on, off, reload, scheduleReload]);

  useEffect(() => {
    // Sign-in, sign-out and a failed fetch all empty `events`, and pruning against nothing would
    // replace the saved selection with defaults and persist them.
    if (knownIds.length === 0) {
      return;
    }
    setSelectedIds((current) => {
      const kept = pruneMissingEventIds(current, events);
      return kept.length > 0 ? kept : defaultCompareEventIds(events);
    });
  }, [events, knownIds]);

  useEffect(() => {
    storage.setJSON(STORAGE_KEYS.EVENT_COMPARE, selectedIds);
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
  }, [mockMode, notifyError, refreshVersion, selectedIds, t]);

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

  // Counted in points, not series: a single surviving point draws no segment, so a legend over
  // blank space is what the user gets.
  const hasSeries = (visibleCompare?.elapsedMinutes.length ?? 0) > 1;

  // The clip keeps only the buckets inside the header range, so a range narrower than one bucket
  // leaves a single point. The response did carry a comparison, so the range is what has to change.
  const clippedToOnePoint =
    !hasSeries &&
    (compare?.elapsedMinutes.length ?? 0) > (visibleCompare?.elapsedMinutes.length ?? 0);

  useEffect(() => hideLineChartTooltip, [hasSeries]);

  const repeatedColors = useMemo(
    () => repeatedColorPositions(visibleCompare?.series ?? []),
    [visibleCompare]
  );

  // Already short ("5m", "1h20m"), so the narrow-plot axis reuses these rather than a second form.
  const labels = useMemo(
    () => (visibleCompare?.elapsedMinutes ?? []).map((minutes) => elapsedLabel(minutes, t)),
    [visibleCompare, t]
  );

  const chartData: ChartData<'line'> = useMemo(() => {
    void themeRevision;
    return {
      labels,
      datasets: (visibleCompare?.series ?? []).map((series, index) => {
        const values = series[metric];
        const color = getThemeColor(`--theme-event-${clampEventColorIndex(series.colorIndex)}`);
        return {
          label: series.name,
          data: values,
          borderColor: color,
          backgroundColor: color,
          borderDash: repeatedColors.has(index) ? EVENT_REPEAT_DASH : undefined,
          fill: false,
          tension: 0.25,
          hidden: hiddenSeries.has(index),
          // An event that has only just started has one value and nulls after it, and a lone value
          // has no neighbour to draw a line to, so at radius 0 the series is invisible.
          pointRadius: values.filter((value) => value !== null).length === 1 ? 4 : 0,
          pointHoverRadius: 6,
          pointHitRadius: 12
        };
      })
    };
  }, [visibleCompare, hiddenSeries, labels, metric, repeatedColors, themeRevision]);

  const chartOptions: ChartOptions<'line'> = useMemo(() => {
    void themeRevision;
    return {
      responsive: true,
      maintainAspectRatio: false,
      animation: { duration: 400, easing: 'easeOutQuart' },
      layout: {
        padding: { top: 4, right: 16, bottom: 4, left: 4 }
      },
      interaction: { mode: 'index', intersect: false },
      plugins: {
        legend: {
          display: false
        },
        tooltip: lineChartTooltip({
          swatchClass: (datasetIndex) => {
            const colorIndex = visibleCompare?.series[datasetIndex]?.colorIndex ?? 1;
            const base = `line-trend-swatch-event-${clampEventColorIndex(colorIndex)}`;
            return repeatedColors.has(datasetIndex) ? `${base} is-dashed` : base;
          },
          title: (items) => {
            const minutes = visibleCompare?.elapsedMinutes[items[0]?.dataIndex ?? 0] ?? 0;
            return t('widgets.eventCompare.elapsed', { time: elapsedLabel(minutes, t) });
          }
        })
      },
      scales: lineChartScales(labels)
    };
  }, [labels, repeatedColors, t, themeRevision, visibleCompare]);

  const legendItems = useMemo(
    () =>
      (visibleCompare?.series ?? []).map((series, index) => {
        const colorClass = `line-trend-swatch-event-${clampEventColorIndex(series.colorIndex)}`;
        return {
          label: series.name,
          colorClass: repeatedColors.has(index) ? `${colorClass} is-dashed` : colorClass,
          hidden: hiddenSeries.has(index)
        };
      }),
    [hiddenSeries, repeatedColors, visibleCompare]
  );

  return (
    <>
      <div className="line-trend-controls">
        {tabControl}
        <div className="line-trend-actions">
          <SegmentedControl
            size="md"
            showLabels
            value={metric}
            onChange={(value) =>
              setMetric(value === 'saved' || value === 'missed' ? value : 'served')
            }
            options={[
              { value: 'served', label: t('widgets.eventCompare.metrics.served') },
              { value: 'saved', label: t('widgets.eventCompare.metrics.saved') },
              { value: 'missed', label: t('widgets.eventCompare.metrics.missed') }
            ]}
          />
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
        </div>
      </div>

      <div className="well-surface dash-line-chart-well">
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
              <Line key={seriesKey} data={chartData} options={chartOptions} />
            </div>
          </>
        ) : (
          <div className="dash-line-chart-placeholder">
            <EmptyState
              icon={GitCompare}
              subtitle={t(
                clippedToOnePoint
                  ? 'widgets.eventCompare.rangeTooShortDesc'
                  : 'widgets.eventCompare.noDataDesc'
              )}
              title={t(
                clippedToOnePoint
                  ? 'widgets.eventCompare.rangeTooShortTitle'
                  : 'widgets.eventCompare.noDataTitle'
              )}
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

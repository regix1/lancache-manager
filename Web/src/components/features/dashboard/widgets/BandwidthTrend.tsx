import React, {
  memo,
  useCallback,
  useEffect,
  useLayoutEffect,
  useMemo,
  useRef,
  useState
} from 'react';
import { Line } from 'react-chartjs-2';
import {
  Chart as ChartJS,
  Filler,
  Legend,
  LinearScale,
  LineElement,
  PointElement,
  Tooltip,
  type ChartData,
  type ChartOptions,
  type Plugin
} from 'chart.js';
import zoomPlugin from 'chartjs-plugin-zoom';
import { Activity } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { useSparklines, useStats } from '@contexts/DashboardDataContext/hooks';
import { useReaderClock } from '@hooks/useReaderClock';
import Badge from '@components/ui/Badge';
import { Button } from '@components/ui/Button';
import LoadingSpinner from '@components/common/LoadingSpinner';
import { EmptyState } from '@components/ui/ManagerCard';
import { ErrorBlock } from '@components/ui/ErrorBlock';
import { HelpNote, HelpPopover, HelpSection } from '@components/ui/HelpPopover';
import { SegmentedControl } from '@components/ui/SegmentedControl';
import { WidgetPanel } from '../WidgetPanel';
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

ChartJS.register(LinearScale, PointElement, LineElement, Filler, Tooltip, Legend, zoomPlugin);

const EMPTY_POINTS: number[] = [];

// The narrowest zoom, in point gaps: three points stay in view.
const MIN_ZOOM_SPAN = 2;

type ChartTab = 'bandwidth' | 'compare';

/** The zoomed span as unix seconds. Times rather than point positions, so a live refresh that
 *  shifts the buckets keeps showing the same stretch of time. */
interface ZoomWindow {
  from: number;
  to: number;
}

// The time at a point position, which can fall between two buckets while zoomed.
const timeAtPosition = (starts: number[], position: number): number => {
  const below = Math.floor(position);
  const above = Math.min(below + 1, starts.length - 1);
  return starts[below] + (position - below) * (starts[above] - starts[below]);
};

// The point position of a time, held to the first and last bucket.
const positionAtTime = (starts: number[], pointCount: number, time: number): number => {
  if (time <= starts[0]) {
    return 0;
  }
  for (let index = 1; index < pointCount; index++) {
    if (time <= starts[index]) {
      return index - 1 + (time - starts[index - 1]) / (starts[index] - starts[index - 1]);
    }
  }
  return pointCount - 1;
};

interface BandwidthTrendProps {
  /** The dashboard's range chip, shown beside the title. */
  badge?: React.ReactNode;
}

const BandwidthTrend: React.FC<BandwidthTrendProps> = memo(({ badge }) => {
  const { t } = useTranslation();
  const clock = useReaderClock();
  const themeRevision = useThemeRevision();
  const { sparklines, loading, failed } = useSparklines();
  // The sparklines hook carries no reason or refetch; both live with the rest of the batch.
  // A batch whose every section failed (`batchFailed`) has one box at the top of the Dashboard.
  const { error, refreshStats, batchFailed } = useStats();
  const loadError = failed ? error : null;
  const [chartTab, setChartTab] = useState<ChartTab>('bandwidth');
  const [zoomed, setZoomed] = useState(false);
  const { hiddenSeries, toggleSeries, seriesKey } = useHiddenSeries();
  const chartRef = useRef<ChartJS<'line'>>(null);
  // The zoom the chart shows, copied after every chart update. A ref rather than state: a drag
  // moves the chart directly many times between renders, so any copy made at render time is
  // already behind it.
  const heldWindowRef = useRef<ZoomWindow | null>(null);

  const bucketMinutes = sparklines?.bucketMinutes ?? 1440;
  const starts = sparklines?.bucketStarts ?? EMPTY_POINTS;
  const saved = sparklines?.bandwidthSaved.data ?? EMPTY_POINTS;
  const served = sparklines?.totalServed.data ?? EMPTY_POINTS;
  const missed = sparklines?.addedToCache.data ?? EMPTY_POINTS;
  const pointCount = Math.min(starts.length, saved.length, served.length, missed.length);
  const hasSeries = pointCount > 0 && hasBandwidthPoints(saved, served);
  const isCompare = chartTab === 'compare';

  useEffect(() => hideLineChartTooltip, [hasSeries, isCompare]);

  // Full labels (used both on the axis and, unabbreviated, in the tooltip title) and their short
  // form for a narrow plot. Only the 'stamp' style (bucketMinutes 180-1439) has room to shrink:
  // 'timeOnly' is already short, and forcing 'dateShort' onto it would drop its only information.
  const labels = useMemo(
    () =>
      starts.slice(0, pointCount).map((start) => bandwidthTickLabel(start, bucketMinutes, clock)),
    [starts, pointCount, bucketMinutes, clock]
  );
  const narrowLabels = useMemo(
    () =>
      bucketMinutes >= 180 && bucketMinutes < 1440
        ? starts.slice(0, pointCount).map((start) => bandwidthTickLabel(start, 1440, clock))
        : labels,
    [starts, pointCount, bucketMinutes, clock, labels]
  );

  // The buckets the chart is drawing, for the zoom keeper below, which runs inside chart updates.
  const bucketsRef = useRef({ starts, pointCount });
  useLayoutEffect(() => {
    bucketsRef.current = { starts, pointCount };
  }, [starts, pointCount]);

  // The zoom plugin moves the time axis by writing its bounds straight into the chart, many times
  // between two renders. This copies what the chart shows after every update, its own included,
  // so the held zoom is never behind the chart.
  const zoomKeeper: Plugin<'line'> = useMemo(
    () => ({
      id: 'bandwidthZoomKeeper',
      afterUpdate(chart) {
        const { min, max } = chart.scales.x;
        const { starts: buckets, pointCount: count } = bucketsRef.current;
        const isZoomed = min > 0 || max < count - 1;
        heldWindowRef.current = isZoomed
          ? { from: timeAtPosition(buckets, min), to: timeAtPosition(buckets, max) }
          : null;
        setZoomed(isZoomed);
      }
    }),
    []
  );

  // The held zoom as point positions in the buckets drawn now, or null for the whole range. A span
  // narrower than the zoom limit means the dashboard range moved away from it.
  const heldBounds = useCallback(() => {
    const held = heldWindowRef.current;
    if (held === null) {
      return null;
    }
    const { starts: buckets, pointCount: count } = bucketsRef.current;
    const min = positionAtTime(buckets, count, held.from);
    const max = positionAtTime(buckets, count, held.to);
    return max - min >= MIN_ZOOM_SPAN ? { min, max } : null;
  }, []);

  // The value axis top kept while a drag or zoom is moving, or null to fit the visible points.
  // It is written into the chart's own options, which every step of the gesture redraws from;
  // the ref carries it into options a data refresh hands over mid-gesture.
  const heldValueMaxRef = useRef<number | null>(null);
  const holdValueAxis = useCallback((chart: ChartJS) => {
    const y = chart.options.scales?.y;
    if (heldValueMaxRef.current === null && y !== undefined) {
      heldValueMaxRef.current = chart.scales.y.max;
      y.max = heldValueMaxRef.current;
    }
  }, []);
  const refitValueAxis = useCallback((chart: ChartJS) => {
    heldValueMaxRef.current = null;
    const y = chart.options.scales?.y;
    if (y !== undefined) {
      y.max = undefined;
    }
    chart.update();
  }, []);

  const resetZoom = useCallback(() => {
    heldWindowRef.current = null;
    const x = chartRef.current?.options.scales?.x;
    if (x !== undefined) {
      x.min = 0;
      x.max = bucketsRef.current.pointCount - 1;
      chartRef.current?.update();
    }
  }, []);

  const chartData: ChartData<'line'> = useMemo(() => {
    void themeRevision;
    // Saved and missed are the cache-hit and cache-miss series, so they read the chart's own
    // hit/miss colours rather than the status green and amber. That is the vocabulary the rest
    // of the charts already use for these two words - the compare chart's hit/miss lines and
    // legend swatches read the same two tokens - and it leaves success and warning to mean a
    // status. Served has no cache meaning, so it stays on the series colour.
    // Only Total served carries an area wash. Hits and misses add up to it, so their areas sat
    // under its own and the three overlapping washes mixed into a muddy band; as plain lines they
    // read against one clean fill. pointBackgroundColor repeats each series' stroke so the hover
    // dot is solid, and its ring in the well colour keeps it readable where two lines cross.
    // Monotone curves never swing below zero beside a spike the way a plain tension curve does.
    const servedColor = getThemeColor('--theme-primary');
    const savedColor = getThemeColor('--theme-chart-cache-hit');
    const missedColor = getThemeColor('--theme-chart-cache-miss');
    const lineStyle = {
      borderWidth: 2,
      cubicInterpolationMode: 'monotone' as const,
      pointRadius: 0,
      pointHoverRadius: 5,
      pointHoverBorderWidth: 2,
      pointHoverBorderColor: getThemeColor('--theme-bg-tertiary')
    };
    // Points sit at their bucket's position on a linear axis rather than in category slots.
    const toPoints = (values: number[]) => values.slice(0, pointCount).map((y, x) => ({ x, y }));
    return {
      datasets: [
        {
          ...lineStyle,
          label: t('widgets.bandwidthTrend.served'),
          data: toPoints(served),
          borderColor: servedColor,
          backgroundColor: getThemeColor('--theme-primary-subtle'),
          pointBackgroundColor: servedColor,
          fill: true,
          hidden: hiddenSeries.has(0)
        },
        {
          ...lineStyle,
          label: t('widgets.bandwidthTrend.saved'),
          data: toPoints(saved),
          borderColor: savedColor,
          pointBackgroundColor: savedColor,
          fill: false,
          hidden: hiddenSeries.has(1)
        },
        {
          ...lineStyle,
          label: t('widgets.bandwidthTrend.missed'),
          data: toPoints(missed),
          borderColor: missedColor,
          pointBackgroundColor: missedColor,
          fill: false,
          hidden: hiddenSeries.has(2)
        }
      ]
    };
  }, [hiddenSeries, missed, pointCount, saved, served, t, themeRevision]);

  const chartOptions: ChartOptions<'line'> = useMemo(() => {
    void themeRevision;
    const scales = lineChartScales(labels, narrowLabels);
    return {
      responsive: true,
      maintainAspectRatio: false,
      animation: { duration: 400, easing: 'easeOutQuart' },
      // Each scroll step redraws at once. Animating every step lets the next step start from a
      // half-finished frame, so the chart trails the wheel.
      transitions: { zoom: { animation: { duration: 0 } } },
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
            if (datasetIndex === 1) {
              return 'line-trend-swatch-success';
            }
            return datasetIndex === 2 ? 'line-trend-swatch-warning' : 'line-trend-swatch-primary';
          },
          title: (items) => labels[items[0].dataIndex]
        }),
        // Zoom moves only the time axis; the value axis then fits the points still in view. It
        // holds still while a drag or zoom is moving and refits once, animated, when it ends:
        // refitting on every step made the lines leap whenever a tall bucket crossed an edge.
        zoom: {
          limits: { x: { min: 0, max: pointCount - 1, minRange: MIN_ZOOM_SPAN } },
          pan: {
            enabled: true,
            mode: 'x',
            // A chart showing its whole range has nowhere to pan, and refusing the drag leaves a
            // finger free to move the tooltip along the line.
            onPanStart: ({ chart }) => {
              if (heldWindowRef.current === null) {
                return false;
              }
              holdValueAxis(chart);
              return true;
            },
            onPanComplete: ({ chart }) => refitValueAxis(chart)
          },
          zoom: {
            // Ctrl keeps plain scrolling for the page. A trackpad pinch arrives as a Ctrl+wheel
            // event, so it zooms the chart without the key.
            wheel: { enabled: true, modifierKey: 'ctrl' },
            pinch: { enabled: true },
            mode: 'x',
            onZoomStart: ({ chart }) => {
              holdValueAxis(chart);
              return true;
            },
            onZoomComplete: ({ chart }) => refitValueAxis(chart)
          }
        }
      },
      scales: {
        ...scales,
        // A linear axis can stop between two buckets, so a drag or a zoom follows the pointer
        // instead of jumping a whole bucket at a time. Ticks stay on whole buckets, the only
        // positions that have a label. The bounds are always set, so the ticks land on fixed
        // multiples of their spacing and slide with the lines, rather than being counted from
        // the left edge and relabelled on every step of a drag.
        x: {
          ...scales.x,
          type: 'linear',
          ticks: { ...scales.x.ticks, precision: 0 },
          // Getters, so the chart reads the held zoom when it applies these options, not when
          // they were built: a drag can move the chart many times in between, and bounds taken
          // at render time would pull it back.
          get min() {
            return heldBounds()?.min ?? 0;
          },
          get max() {
            return heldBounds()?.max ?? bucketsRef.current.pointCount - 1;
          }
        },
        y: {
          ...scales.y,
          get max() {
            return heldValueMaxRef.current ?? undefined;
          }
        }
      }
    };
  }, [heldBounds, holdValueAxis, labels, narrowLabels, pointCount, refitValueAxis, themeRevision]);

  // The hint and Reset zoom share one slot, so swapping them never moves the legend or chart.
  const zoomControl = useMemo(
    () => (
      <div className="line-trend-zoom-slot">
        <Badge variant="neutral" className={zoomed ? 'is-idle' : undefined}>
          <span className="line-trend-zoom-hint-pointer">
            {t('widgets.bandwidthTrend.zoomHintPointer')}
          </span>
          <span className="line-trend-zoom-hint-touch">
            {t('widgets.bandwidthTrend.zoomHintTouch')}
          </span>
        </Badge>
        <Button
          variant="filled"
          color="secondary"
          size="xs"
          className={zoomed ? undefined : 'is-idle'}
          onClick={resetZoom}
        >
          {t('widgets.bandwidthTrend.resetZoom')}
        </Button>
      </div>
    ),
    [resetZoom, t, zoomed]
  );

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
      },
      {
        label: t('widgets.bandwidthTrend.missed'),
        colorClass: 'line-trend-swatch-warning',
        hidden: hiddenSeries.has(2)
      }
    ],
    [hiddenSeries, t]
  );

  // A fresh element every render defeats the memo on EventCompareChart, which re-renders it on
  // every sparkline refresh.
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

  const loadErrorBlock =
    loadError === null || batchFailed ? null : (
      <ErrorBlock
        className="mb-3 last:mb-0"
        title={t('widgets.bandwidthTrend.loadFailed')}
        message={loadError}
        retryLabel={t('common.retry')}
        onRetry={() => void refreshStats(true)}
      />
    );

  return (
    <WidgetPanel className="widget-card--wide line-trend-card">
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
                <HelpNote type="info">{t('widgets.bandwidthTrend.help.zoom')}</HelpNote>
              </>
            )}
          </HelpPopover>
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
              {loadErrorBlock}
              <LineChartLegend items={legendItems} onToggle={toggleSeries} action={zoomControl} />
              <div className={`dash-line-chart is-zoomable${zoomed ? ' is-zoomed' : ''}`}>
                <Line
                  key={seriesKey}
                  ref={chartRef}
                  data={chartData}
                  options={chartOptions}
                  plugins={[zoomKeeper]}
                />
              </div>
            </>
          ) : loadError !== null ? (
            loadErrorBlock
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
      {/* The bucket size the chart drew at, beside the range it drew for: both are chips, both
          neutral, so the row reads as one statement about this card's data. */}
      <div className="dash-range-footer">
        {hasSeries ? (
          <Badge variant="neutral">{t(`widgets.bandwidthTrend.resolution.${bucketMinutes}`)}</Badge>
        ) : null}
        {badge}
      </div>
    </WidgetPanel>
  );
});

BandwidthTrend.displayName = 'BandwidthTrend';

export default BandwidthTrend;

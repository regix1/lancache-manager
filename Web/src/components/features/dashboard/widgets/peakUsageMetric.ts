import { type HourlyActivityItem } from '../../../../types';

/**
 * Which figure the Peak Usage Hours heatmap shades by, and which one decides the busiest hour.
 * The two disagree whenever one large game outweighs many small ones, which is the whole reason
 * the reader gets to pick.
 */
export type PeakUsageMetric = 'bytes' | 'downloads';

/** The per-hour figure the chosen metric reads. */
export function hourlyMetricValue(hour: HourlyActivityItem, metric: PeakUsageMetric): number {
  return metric === 'bytes' ? hour.bytesServed : hour.downloads;
}

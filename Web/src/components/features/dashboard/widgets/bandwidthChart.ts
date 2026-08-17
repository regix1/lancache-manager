import { formatTimestamp, type ReaderClock } from '@utils/dateTimeFormat';

type BandwidthResolution = '15' | '30' | '60' | '180' | '1440';

export function bandwidthResolution(bucketMinutes: number): BandwidthResolution {
  if (bucketMinutes <= 15) {
    return '15';
  }
  if (bucketMinutes <= 30) {
    return '30';
  }
  if (bucketMinutes <= 60) {
    return '60';
  }
  if (bucketMinutes <= 180) {
    return '180';
  }
  return '1440';
}

export function bandwidthTickLabel(
  startUnix: number,
  bucketMinutes: number,
  clock: ReaderClock
): string {
  const style = bucketMinutes >= 1440 ? 'dateShort' : bucketMinutes >= 180 ? 'stamp' : 'timeOnly';
  return formatTimestamp(new Date(startUnix * 1000), {
    ...clock,
    forceYear: false,
    style
  });
}

export function hasBandwidthPoints(saved: number[], served: number[]): boolean {
  return saved.some((value) => value > 0) || served.some((value) => value > 0);
}

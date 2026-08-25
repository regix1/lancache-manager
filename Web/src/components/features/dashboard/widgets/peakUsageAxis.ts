/**
 * Peak Usage is a 12-column clock matrix. Rows are the two halves of the day
 * (hours 0–11 and 12–23). Columns are clock hours 0–11, so 03:00 and 15:00 share
 * a column.
 *
 * 12-hour clocks name those rows AM / PM and share one axis (12 / 3 / 6 / 9).
 * 24-hour clocks do not use AM / PM: each row gets its own axis of actual hours
 * (00 / 03 / 06 / 09 above, 12 / 15 / 18 / 21 below).
 *
 * Axis ticks mark every third column, the 12-column equivalent of 00 / 06 / 12 / 18
 * on a 24-hour strip.
 */
export const PEAK_USAGE_ROW_HOURS = 12;

const PEAK_USAGE_AXIS_COLUMNS = [0, 3, 6, 9] as const;

export function peakUsageColumn(hour: number): number {
  return hour % PEAK_USAGE_ROW_HOURS;
}

export function peakUsageRow(hour: number): 0 | 1 {
  return hour < PEAK_USAGE_ROW_HOURS ? 0 : 1;
}

export function isPeakUsageAxisColumn(column: number): boolean {
  return (PEAK_USAGE_AXIS_COLUMNS as readonly number[]).includes(column);
}

/**
 * Tick text for one column. 12-hour mode is a clock face (12, 3, 6, 9) and ignores
 * the row. 24-hour mode is the real hour: row 0 column 3 is 03, row 1 column 3 is 15.
 */
export function peakUsageClockLabel(
  column: number,
  use24HourFormat: boolean,
  rowStartHour = 0
): string {
  if (use24HourFormat) {
    return (rowStartHour + column).toString().padStart(2, '0');
  }
  return column === 0 ? '12' : String(column);
}

import { useMemo } from 'react';
import { useTimezone } from '@contexts/useTimezone';
import type { ReaderClock } from '@utils/dateTimeFormat';

/**
 * The clock the reader is currently on, in the shape every formatter takes.
 *
 * Assembling it by hand is what lets the three flags drift: a component that passes two of them and
 * leaves the third to module state renders the clock the reader has just switched away from, and
 * then stops changing. `refreshKey` is a dependency for the same reason it is one in
 * useFormattedDateTime - a save that echoes the flags back unchanged still has to reach the
 * formatters. [17]
 */
export const useReaderClock = (): ReaderClock => {
  const { useLocalTimezone, useUtcTimezone, use24HourFormat, refreshKey } = useTimezone();

  return useMemo<ReaderClock>(
    () => ({
      useLocalTimezone,
      useUtc: useUtcTimezone,
      use24Hour: use24HourFormat
    }),
    // eslint-disable-next-line react-hooks/exhaustive-deps -- refreshKey intentionally forces recomputation
    [useLocalTimezone, useUtcTimezone, use24HourFormat, refreshKey]
  );
};

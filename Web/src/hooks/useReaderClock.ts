import { useMemo } from 'react';
import { useTranslation } from 'react-i18next';
import { useTimezone } from '@contexts/useTimezone';
import type { ReaderClock } from '@utils/dateTimeFormat';

/**
 * The clock the reader is currently on, in the shape every formatter takes.
 *
 * Assembling it by hand is what lets the three flags drift: a component that passes two of them and
 * leaves the third to module state renders the clock the reader has just switched away from, and
 * then stops changing. `refreshKey` is a dependency for the same reason it is one in
 * useFormattedDateTime - a save that echoes the flags back unchanged still has to reach the
 * formatters. The app language is one too: formatTimestamp picks the date's form from it, and every
 * memoized stamp is keyed on this object, so a language switch has to hand out a new one.
 */
export const useReaderClock = (): ReaderClock => {
  const { useLocalTimezone, useUtcTimezone, use24HourFormat, refreshKey } = useTimezone();
  const { i18n } = useTranslation();

  return useMemo<ReaderClock>(
    () => ({
      useLocalTimezone,
      useUtc: useUtcTimezone,
      use24Hour: use24HourFormat
    }),
    // eslint-disable-next-line react-hooks/exhaustive-deps -- refreshKey and the language intentionally force recomputation
    [useLocalTimezone, useUtcTimezone, use24HourFormat, refreshKey, i18n.language]
  );
};

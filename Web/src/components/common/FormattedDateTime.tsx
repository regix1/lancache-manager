import React from 'react';
import { useFormattedDateTime } from '@hooks/useFormattedDateTime';

interface FormattedDateTimeProps {
  /** ISO timestamp. Undefined renders the hook's own placeholder rather than blank. */
  timestamp: string | undefined;
}

/**
 * Wrapper so useFormattedDateTime can be called per row: the value re-renders on
 * timezone and time-format preference changes without the parent re-rendering.
 */
export const FormattedTimestamp: React.FC<FormattedDateTimeProps> = ({ timestamp }) => {
  const formattedTime = useFormattedDateTime(timestamp);
  return <>{formattedTime}</>;
};

/** The same value inside a truncating table cell. */
export const FormattedDateCell: React.FC<FormattedDateTimeProps> = ({ timestamp }) => {
  const formatted = useFormattedDateTime(timestamp);
  return (
    <span className="block truncate text-xs text-themed-secondary whitespace-nowrap">
      {formatted}
    </span>
  );
};

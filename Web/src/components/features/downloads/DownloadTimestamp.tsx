import { Clock } from 'lucide-react';
import { Tooltip } from '@components/ui/Tooltip';
import { useFormattedDateTime } from '@hooks/useFormattedDateTime';
import { formatRelativeTime } from '@utils/formatters';

interface DownloadTimestampProps {
  dateString: string | null | undefined;
  /** Normal/Card: absolute date+time shown directly. Compact: absolute is tooltip-only. */
  showAbsoluteInline?: boolean;
  showIcon?: boolean;
  iconSize?: number;
  className?: string;
}

export function DownloadTimestamp({
  dateString,
  showAbsoluteInline = false,
  showIcon = false,
  iconSize = 14,
  className
}: DownloadTimestampProps) {
  const absolute = useFormattedDateTime(dateString ?? undefined);
  const parsedTime = dateString ? new Date(dateString).getTime() : NaN;
  const isValid = !Number.isNaN(parsedTime);
  const relative = formatRelativeTime(dateString);

  if (showAbsoluteInline) {
    return (
      <span className={`inline-flex items-center gap-1.5 ${className ?? ''}`}>
        {showIcon && <Clock size={iconSize} className="flex-shrink-0" />}
        <span>{relative}</span>
        {isValid && <span className="text-[var(--theme-text-muted)]">· {absolute}</span>}
      </span>
    );
  }

  return (
    <Tooltip content={isValid ? absolute : relative} className={className}>
      {showIcon && <Clock size={iconSize} className="flex-shrink-0 inline mr-1" />}
      {relative}
    </Tooltip>
  );
}

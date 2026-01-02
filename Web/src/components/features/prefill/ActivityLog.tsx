import { useEffect, useRef, memo } from 'react';
import {
  CheckCircle2,
  AlertCircle,
  Info,
  Download,
  LogIn,
  Loader2,
  Clock,
  XCircle,
  Gamepad2
} from 'lucide-react';

export type LogEntryType =
  | 'info'
  | 'success'
  | 'error'
  | 'warning'
  | 'download'
  | 'auth'
  | 'progress'
  | 'command';

export interface LogEntry {
  id: string;
  timestamp: Date;
  type: LogEntryType;
  message: string;
  details?: string;
}

interface ActivityLogProps {
  entries: LogEntry[];
  maxHeight?: string;
  className?: string;
}

const LogIcon = memo(({ type }: { type: LogEntryType }) => {
  const iconClass = "h-4 w-4 flex-shrink-0";

  switch (type) {
    case 'success':
      return <CheckCircle2 className={`${iconClass} text-green-500`} />;
    case 'error':
      return <XCircle className={`${iconClass} text-red-500`} />;
    case 'warning':
      return <AlertCircle className={`${iconClass} text-yellow-500`} />;
    case 'download':
      return <Download className={`${iconClass} text-blue-500`} />;
    case 'auth':
      return <LogIn className={`${iconClass} text-purple-500`} />;
    case 'progress':
      return <Loader2 className={`${iconClass} text-blue-500 animate-spin`} />;
    case 'command':
      return <Gamepad2 className={`${iconClass} text-cyan-500`} />;
    default:
      return <Info className={`${iconClass} text-themed-secondary`} />;
  }
});

LogIcon.displayName = 'LogIcon';

const formatTime = (date: Date): string => {
  return date.toLocaleTimeString('en-US', {
    hour12: false,
    hour: '2-digit',
    minute: '2-digit',
    second: '2-digit'
  });
};

const LogEntryRow = memo(({ entry }: { entry: LogEntry }) => (
  <div className="flex items-start gap-2 py-1.5 px-3 hover:bg-themed-surface-hover transition-colors">
    <span className="text-xs text-themed-tertiary font-mono flex-shrink-0 pt-0.5">
      {formatTime(entry.timestamp)}
    </span>
    <LogIcon type={entry.type} />
    <div className="flex-1 min-w-0">
      <span className="text-sm text-themed-primary break-words">
        {entry.message}
      </span>
      {entry.details && (
        <p className="text-xs text-themed-tertiary mt-0.5 break-words">
          {entry.details}
        </p>
      )}
    </div>
  </div>
));

LogEntryRow.displayName = 'LogEntryRow';

export function ActivityLog({ entries, maxHeight = '400px', className = '' }: ActivityLogProps) {
  const containerRef = useRef<HTMLDivElement>(null);
  const shouldAutoScroll = useRef(true);

  // Auto-scroll to bottom when new entries are added
  useEffect(() => {
    if (containerRef.current && shouldAutoScroll.current) {
      containerRef.current.scrollTop = containerRef.current.scrollHeight;
    }
  }, [entries]);

  // Detect if user scrolled up (disable auto-scroll)
  const handleScroll = () => {
    if (!containerRef.current) return;
    const { scrollTop, scrollHeight, clientHeight } = containerRef.current;
    // If scrolled within 50px of bottom, enable auto-scroll
    shouldAutoScroll.current = scrollHeight - scrollTop - clientHeight < 50;
  };

  return (
    <div
      ref={containerRef}
      onScroll={handleScroll}
      className={`overflow-y-auto bg-themed-surface rounded-lg border border-themed ${className}`}
      style={{ maxHeight }}
    >
      {entries.length === 0 ? (
        <div className="flex flex-col items-center justify-center py-8 text-themed-tertiary">
          <Clock className="h-8 w-8 mb-2 opacity-50" />
          <p className="text-sm">Waiting for activity...</p>
        </div>
      ) : (
        <div className="divide-y divide-themed">
          {entries.map((entry) => (
            <LogEntryRow key={entry.id} entry={entry} />
          ))}
        </div>
      )}
    </div>
  );
}

// Helper to create log entries
export function createLogEntry(
  type: LogEntryType,
  message: string,
  details?: string
): LogEntry {
  return {
    id: `${Date.now()}-${Math.random().toString(36).substr(2, 9)}`,
    timestamp: new Date(),
    type,
    message,
    details
  };
}

export default ActivityLog;

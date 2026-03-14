import { createContext } from 'react';

/**
 * PICS Progress Interface
 * Matches the structure returned by /api/depots/rebuild/progress
 */
interface PicsProgress {
  // Core status
  isProcessing: boolean;
  status: string;

  // Progress metrics
  totalApps: number;
  processedApps: number;
  totalBatches: number;
  processedBatches: number;
  progressPercent: number;

  // Depot information
  depotMappingsFound: number;
  depotMappingsFoundInSession?: number;

  // Scheduling (what the API actually returns)
  crawlIntervalHours: number;
  crawlIncrementalMode: boolean | string; // true (incremental), false (full), or "github" (PICS only)
  lastCrawlTime?: string; // ISO 8601 datetime string
  nextCrawlIn?: number; // Seconds remaining until next crawl

  // Additional metadata
  startTime?: string;
  lastChangeNumber?: number;
  failedBatches?: number;
  remainingApps?: number[];

  // Scan flags
  isReady?: boolean;
  lastScanWasForced?: boolean;
  automaticScanSkipped?: boolean;

  // Connection status
  isConnected?: boolean;
  isLoggedOn?: boolean;

  // Web API availability (for Full/Incremental scans)
  isWebApiAvailable?: boolean;

  // Error handling
  errorMessage?: string | null;
}

interface PicsProgressContextType {
  progress: PicsProgress | null;
  isLoading: boolean;
  refreshProgress: () => Promise<void>;
  updateProgress: (updater: (prev: PicsProgress | null) => PicsProgress | null) => void;
}

export const PicsProgressContext = createContext<PicsProgressContextType | undefined>(undefined);

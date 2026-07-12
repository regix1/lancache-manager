export interface IntegrityReviewSnapshot {
  scanId: string | null;
  reviewOnlyServiceCounts: Record<string, number>;
  reviewOnlyTotal: number;
  isLoading: boolean;
  isBusy: boolean;
}

export interface HistoricalEvidencePurgeTarget {
  service?: string;
}

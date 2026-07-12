interface CorruptionServiceCountRow {
  service: string;
  count: number;
}

interface CorruptionCountProjection {
  rows: CorruptionServiceCountRow[];
  total: number;
  serviceTotal: number;
  isConsistent: boolean;
}

export const projectCorruptionCounts = (
  counts: Record<string, number>
): CorruptionCountProjection => {
  const allRows = Object.entries(counts).map(([service, count]) => ({
    service,
    count
  }));
  const rows = allRows
    .filter((row) => row.count > 0)
    .sort((left, right) => right.count - left.count || left.service.localeCompare(right.service));

  return {
    rows,
    total: rows.reduce((total, row) => total + row.count, 0),
    serviceTotal: rows.length,
    isConsistent: allRows.every((row) => Number.isInteger(row.count) && row.count >= 0)
  };
};

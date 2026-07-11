interface CorruptionServiceCountRow {
  service: string;
  all: number;
  removable: number;
  reviewOnly: number;
}

interface CorruptionCountProjection {
  rows: CorruptionServiceCountRow[];
  allTotal: number;
  removableTotal: number;
  reviewOnlyTotal: number;
  removableServiceTotal: number;
  reviewOnlyServiceTotal: number;
  isConsistent: boolean;
}

const countFor = (counts: Record<string, number>, service: string): number => counts[service] ?? 0;

export const projectCorruptionCounts = (
  allCounts: Record<string, number>,
  removableCounts: Record<string, number>,
  reviewOnlyCounts: Record<string, number>
): CorruptionCountProjection => {
  const serviceKeys = new Set([
    ...Object.keys(allCounts),
    ...Object.keys(removableCounts),
    ...Object.keys(reviewOnlyCounts)
  ]);

  const allRows = [...serviceKeys].map((service) => ({
    service,
    all: countFor(allCounts, service),
    removable: countFor(removableCounts, service),
    reviewOnly: countFor(reviewOnlyCounts, service)
  }));
  const rows = allRows
    .filter((row) => row.all > 0 || row.removable > 0 || row.reviewOnly > 0)
    .sort((left, right) => right.all - left.all || left.service.localeCompare(right.service));

  return {
    rows,
    allTotal: rows.reduce((total, row) => total + row.all, 0),
    removableTotal: rows.reduce((total, row) => total + row.removable, 0),
    reviewOnlyTotal: rows.reduce((total, row) => total + row.reviewOnly, 0),
    removableServiceTotal: rows.filter((row) => row.removable > 0).length,
    reviewOnlyServiceTotal: rows.filter((row) => row.reviewOnly > 0).length,
    isConsistent: allRows.every(
      (row) =>
        Number.isInteger(row.all) &&
        Number.isInteger(row.removable) &&
        Number.isInteger(row.reviewOnly) &&
        row.all >= 0 &&
        row.removable >= 0 &&
        row.reviewOnly >= 0 &&
        row.all === row.removable + row.reviewOnly
    )
  };
};

// Hit-rate classification shared by every view that colours a cache-hit percentage: the retro row
// renderer (gauge colour, accent stripe), the recent-downloads list, the clients table and the top
// clients table. Four bands so the worst outcomes read as more severe than a plain miss. [18]
export type EfficiencyTier = 'success' | 'warning' | 'error' | 'critical';

export const efficiencyTier = (percent: number): EfficiencyTier => {
  if (percent >= 75) return 'success';
  if (percent >= 50) return 'warning';
  if (percent >= 25) return 'error';
  return 'critical';
};

// The ink each band paints its percentage in, defined in styles/features/recent-downloads.css.
// Class names must appear as literal strings, never built from a template: the content scanner reads
// source text, and a template inside an arbitrary-value class also crashes the build's CSS minifier.
export const HIT_TIER_CLASS: Record<EfficiencyTier, string> = {
  success: 'hit-tier-success',
  warning: 'hit-tier-warning',
  error: 'hit-tier-error',
  critical: 'hit-tier-critical'
};

import type { ServiceStat } from '@/types';

export type TabId = 'service' | 'hit-ratio' | 'bandwidth' | 'misses' | 'games';

export interface ChartDataset {
  id: string;
  data: number[];
  originalData?: number[];
  backgroundColor: string[];
  borderColor: string;
  borderWidth: number;
  borderRadius?: number;
  spacing?: number;
  hoverOffset?: number;
}

export interface ChartData {
  labels: string[];
  datasets: ChartDataset[];
  total: number;
  isEmpty: boolean;
  gameSliceExtras?: GameSliceExtra[];
}

export interface LegendItem {
  label: string;
  value: number;
  color: string;
  percentage: number;
  valueLabel?: string;
  colorClassName?: string;
}

export interface ServiceAnalyticsChartProps {
  serviceStats: ServiceStat[];
  timeRange?: string;
  glassmorphism?: boolean;
  loading?: boolean;
}

export interface GameSliceExtra {
  cacheFiles: number;
  service: string;
}

export interface DoughnutChartProps {
  labels: string[];
  datasets: ChartDataset[];
  total: number;
  centerLabel: string;
  gameSliceExtras?: GameSliceExtra[];
}

export interface ChartLegendProps {
  items: LegendItem[];
}

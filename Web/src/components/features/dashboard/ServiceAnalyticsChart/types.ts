import type { ServiceStat } from '@/types';

export type TabId = 'service' | 'hit-ratio' | 'bandwidth';

export interface TabConfig {
  id: TabId;
  name: string;
  shortName: string;
  icon: React.ElementType;
}

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
}

export interface LegendItem {
  label: string;
  value: number;
  color: string;
  percentage: number;
}

export interface ServiceAnalyticsChartProps {
  serviceStats: ServiceStat[];
  timeRange?: string;
  glassmorphism?: boolean;
}

export interface DoughnutChartProps {
  labels: string[];
  datasets: ChartDataset[];
  total: number;
  centerLabel: string;
}

export interface ChartLegendProps {
  items: LegendItem[];
}

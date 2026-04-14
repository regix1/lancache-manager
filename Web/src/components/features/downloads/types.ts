import type { Download, DownloadGroup } from '../../../types';

export type HeaderRowKind = 'multiple' | 'single' | 'individual';

export type FlatRow =
  | { kind: 'header'; id: string; variant: HeaderRowKind }
  | { kind: 'item'; id: string; item: Download | DownloadGroup };

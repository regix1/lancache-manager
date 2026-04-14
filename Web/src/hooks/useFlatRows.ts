import { useMemo } from 'react';
import type { Download, DownloadGroup } from '../types';
import type { FlatRow } from '../components/features/downloads/types';

interface UseFlatRowsOptions {
  items: (Download | DownloadGroup)[];
  groupByFrequency: boolean;
}

/**
 * Flattens a list of downloads/groups into a row array for list virtualization,
 * emitting at most one section-header row per frequency bucket (multiple,
 * single, individual) when `groupByFrequency` is true.
 *
 * The result is a discriminated union (`FlatRow`) so consumers can render a
 * header or a real download/group card and the virtualizer can index over the
 * combined sequence uniformly.
 */
export function useFlatRows({ items, groupByFrequency }: UseFlatRowsOptions): FlatRow[] {
  return useMemo<FlatRow[]>(() => {
    const rows: FlatRow[] = [];
    let multipleHeaderEmitted = false;
    let singleHeaderEmitted = false;
    let individualHeaderEmitted = false;

    for (const item of items) {
      const isGroup = 'downloads' in item;
      if (groupByFrequency) {
        if (isGroup) {
          const group = item as DownloadGroup;
          if (group.count > 1 && !multipleHeaderEmitted) {
            multipleHeaderEmitted = true;
            rows.push({ kind: 'header', id: 'header-multiple', variant: 'multiple' });
          } else if (group.count === 1 && !singleHeaderEmitted) {
            singleHeaderEmitted = true;
            rows.push({ kind: 'header', id: 'header-single', variant: 'single' });
          }
        } else if (!individualHeaderEmitted) {
          individualHeaderEmitted = true;
          rows.push({ kind: 'header', id: 'header-individual', variant: 'individual' });
        }
      }
      const rowId = isGroup ? (item as DownloadGroup).id : `download-${(item as Download).id}`;
      rows.push({ kind: 'item', id: rowId, item });
    }
    return rows;
  }, [items, groupByFrequency]);
}

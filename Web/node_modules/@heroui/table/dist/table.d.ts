import * as _heroui_system from '@heroui/system';
import { UseTableProps } from './use-table.js';
import '@react-types/table';
import 'react';
import '@heroui/theme';
import '@react-stately/virtualizer';
import '@react-types/shared';
import '@react-stately/table';
import '@react-aria/table';
import '@heroui/react-utils';
import '@heroui/checkbox';

interface TableProps<T = object> extends Omit<UseTableProps<T>, "isSelectable" | "isMultiSelectable"> {
    isVirtualized?: boolean;
    rowHeight?: number;
    maxTableHeight?: number;
}
declare const Table: _heroui_system.InternalForwardRefRenderFunction<"table", TableProps<object>, never>;

export { type TableProps, Table as default };

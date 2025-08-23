import * as _heroui_system from '@heroui/system';
import { GridNode } from '@react-types/grid';
import { TableRowProps as TableRowProps$1 } from './base/table-row.js';
import { ValuesType } from './use-table.js';
import '@react-types/table';
import 'react';
import '@heroui/theme';
import '@react-stately/virtualizer';
import '@react-types/shared';
import '@react-stately/table';
import '@react-aria/table';
import '@heroui/react-utils';
import '@heroui/checkbox';

interface TableRowProps<T = object> extends Omit<TableRowProps$1, "children"> {
    /**
     * The table row.
     */
    node: GridNode<T>;
    slots: ValuesType["slots"];
    state: ValuesType["state"];
    isSelectable?: ValuesType["isSelectable"];
    classNames?: ValuesType["classNames"];
}
declare const TableRow: _heroui_system.InternalForwardRefRenderFunction<"tr", TableRowProps<object>, never>;

export { type TableRowProps, TableRow as default };

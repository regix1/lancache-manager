import * as _heroui_system from '@heroui/system';
import { HTMLHeroUIProps } from '@heroui/system';
import { GridNode } from '@react-types/grid';
import { Key } from 'react';
import { ValuesType } from './use-table.mjs';
import '@react-types/table';
import '@heroui/theme';
import '@react-stately/virtualizer';
import '@react-types/shared';
import '@react-stately/table';
import '@react-aria/table';
import '@heroui/react-utils';
import '@heroui/checkbox';

interface TableCheckboxCellProps<T = object> extends HTMLHeroUIProps<"td"> {
    /**
     * The key of the table row.
     */
    rowKey: Key;
    /**
     * The table cell.
     */
    node: GridNode<T>;
    slots: ValuesType["slots"];
    state: ValuesType["state"];
    color: ValuesType["color"];
    disableAnimation: ValuesType["disableAnimation"];
    checkboxesProps: ValuesType["checkboxesProps"];
    selectionMode: ValuesType["selectionMode"];
    classNames?: ValuesType["classNames"];
}
declare const TableCheckboxCell: _heroui_system.InternalForwardRefRenderFunction<"td", TableCheckboxCellProps<object>, never>;

export { type TableCheckboxCellProps, TableCheckboxCell as default };

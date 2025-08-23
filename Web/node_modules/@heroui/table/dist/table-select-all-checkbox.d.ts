import * as _heroui_system from '@heroui/system';
import { HTMLHeroUIProps } from '@heroui/system';
import { GridNode } from '@react-types/grid';
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

interface TableSelectAllCheckboxProps<T = object> extends HTMLHeroUIProps<"th"> {
    /**
     * The table column.
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
declare const TableSelectAllCheckbox: _heroui_system.InternalForwardRefRenderFunction<"th", TableSelectAllCheckboxProps<object>, never>;

export { type TableSelectAllCheckboxProps, TableSelectAllCheckbox as default };

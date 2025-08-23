import * as _heroui_system from '@heroui/system';
import { HTMLHeroUIProps } from '@heroui/system';
import { ValuesType } from './use-table.mjs';
import '@react-types/table';
import 'react';
import '@heroui/theme';
import '@react-stately/virtualizer';
import '@react-types/shared';
import '@react-stately/table';
import '@react-aria/table';
import '@heroui/react-utils';
import '@heroui/checkbox';

interface TableBodyProps extends HTMLHeroUIProps<"tbody"> {
    slots: ValuesType["slots"];
    collection: ValuesType["collection"];
    state: ValuesType["state"];
    isSelectable: ValuesType["isSelectable"];
    color: ValuesType["color"];
    disableAnimation: ValuesType["disableAnimation"];
    checkboxesProps: ValuesType["checkboxesProps"];
    selectionMode: ValuesType["selectionMode"];
    classNames?: ValuesType["classNames"];
}
declare const TableBody: _heroui_system.InternalForwardRefRenderFunction<"tbody", TableBodyProps, never>;

export { type TableBodyProps, TableBody as default };

import * as _heroui_system from '@heroui/system';
import { HTMLHeroUIProps } from '@heroui/system';
import { Virtualizer } from '@tanstack/react-virtual';
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

interface VirtualizedTableBodyProps extends HTMLHeroUIProps<"tbody"> {
    slots: ValuesType["slots"];
    collection: ValuesType["collection"];
    state: ValuesType["state"];
    isSelectable: ValuesType["isSelectable"];
    color: ValuesType["color"];
    disableAnimation: ValuesType["disableAnimation"];
    checkboxesProps: ValuesType["checkboxesProps"];
    selectionMode: ValuesType["selectionMode"];
    classNames?: ValuesType["classNames"];
    rowVirtualizer: Virtualizer<any, Element>;
}
declare const VirtualizedTableBody: _heroui_system.InternalForwardRefRenderFunction<"tbody", VirtualizedTableBodyProps, never>;

export { type VirtualizedTableBodyProps, VirtualizedTableBody as default };

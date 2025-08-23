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

interface TableHeaderRowProps<T = object> extends HTMLHeroUIProps<"tr"> {
    /**
     * The table node to render.
     */
    node: GridNode<T>;
    slots: ValuesType["slots"];
    state: ValuesType["state"];
    classNames?: ValuesType["classNames"];
}
declare const TableHeaderRow: _heroui_system.InternalForwardRefRenderFunction<"tr", TableHeaderRowProps<object>, never>;

export { type TableHeaderRowProps, TableHeaderRow as default };

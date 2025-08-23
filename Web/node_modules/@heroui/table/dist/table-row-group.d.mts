import * as react from 'react';
import { HTMLHeroUIProps } from '@heroui/system';
import { ValuesType } from './use-table.mjs';
import '@react-types/table';
import '@heroui/theme';
import '@react-stately/virtualizer';
import '@react-types/shared';
import '@react-stately/table';
import '@react-aria/table';
import '@heroui/react-utils';
import '@heroui/checkbox';

interface TableRowGroupProps extends HTMLHeroUIProps<"thead"> {
    slots: ValuesType["slots"];
    classNames?: ValuesType["classNames"];
}
declare const TableRowGroup: react.ForwardRefExoticComponent<TableRowGroupProps & react.RefAttributes<HTMLTableSectionElement>>;

export { type TableRowGroupProps, TableRowGroup as default };

import * as react_jsx_runtime from 'react/jsx-runtime';
import { ReactNode } from 'react';
import { UseDropdownProps } from './use-dropdown.js';
import '@heroui/popover';
import '@react-types/menu';
import '@heroui/system';
import '@heroui/react-utils';
import '@heroui/menu';

interface DropdownProps extends UseDropdownProps {
    /**
     * The content of the dropdown. It is usually the `DropdownTrigger`,
     * and `DropdownMenu`
     */
    children: ReactNode[];
}
declare const Dropdown: {
    (props: DropdownProps): react_jsx_runtime.JSX.Element;
    displayName: string;
};

export { type DropdownProps, Dropdown as default };

import * as react_jsx_runtime from 'react/jsx-runtime';
import { ScrollShadowProps } from '@heroui/scroll-shadow';
import { VirtualizationProps } from './listbox.js';
import { UseListboxReturn } from './use-listbox.js';
import 'react';
import 'tailwind-variants';
import '@heroui/system';
import '@react-types/shared';
import '@react-aria/listbox';
import '@heroui/theme';
import '@react-stately/list';
import '@heroui/react-utils';
import './listbox-item.js';
import './use-listbox-item.js';
import './base/listbox-item-base.js';
import '@heroui/aria-utils';

interface Props extends UseListboxReturn {
    isVirtualized?: boolean;
    virtualization?: VirtualizationProps;
    scrollShadowProps?: Partial<ScrollShadowProps>;
}
declare const VirtualizedListbox: (props: Props) => react_jsx_runtime.JSX.Element;

export { VirtualizedListbox as default };

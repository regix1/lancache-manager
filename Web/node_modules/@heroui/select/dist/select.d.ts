import '@heroui/listbox';
import { ReactElement } from 'react';
import { UseSelectProps } from './use-select.js';
import './hidden-select.js';
import '@react-types/shared';
import '@heroui/system';
import '@heroui/theme';
import '@heroui/react-utils';
import '@heroui/popover';
import '@heroui/scroll-shadow';
import '@heroui/use-aria-multiselect';
import '@heroui/spinner';
import 'react/jsx-runtime';

interface Props<T> extends UseSelectProps<T> {
}
type SelectProps<T extends object = object> = Props<T>;
declare const Select: <T extends object>(props: SelectProps<T>) => ReactElement;

export { type SelectProps, Select as default };

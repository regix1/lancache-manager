import { ReactElement } from 'react';
import { UseMenuProps } from './use-menu.js';
import '@heroui/system';
import '@react-types/menu';
import '@react-aria/menu';
import '@heroui/theme';
import '@react-stately/tree';
import '@heroui/react-utils';
import './menu-item.js';
import 'react/jsx-runtime';
import './use-menu-item.js';
import './base/menu-item-base.js';
import '@react-types/shared';
import '@heroui/aria-utils';
import 'tailwind-variants';

interface Props<T> extends UseMenuProps<T> {
}
type MenuProps<T extends object = object> = Props<T>;
declare const Menu: <T extends object>(props: MenuProps<T>) => ReactElement;

export { type MenuProps, Menu as default };

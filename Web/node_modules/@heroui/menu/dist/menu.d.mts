import { ReactElement } from 'react';
import { UseMenuProps } from './use-menu.mjs';
import '@heroui/system';
import '@react-types/menu';
import '@react-aria/menu';
import '@heroui/theme';
import '@react-stately/tree';
import '@heroui/react-utils';
import './menu-item.mjs';
import 'react/jsx-runtime';
import './use-menu-item.mjs';
import './base/menu-item-base.mjs';
import '@react-types/shared';
import '@heroui/aria-utils';
import 'tailwind-variants';

interface Props<T> extends UseMenuProps<T> {
}
type MenuProps<T extends object = object> = Props<T>;
declare const Menu: <T extends object>(props: MenuProps<T>) => ReactElement;

export { type MenuProps, Menu as default };

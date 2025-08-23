import * as react_jsx_runtime from 'react/jsx-runtime';
import { UseMenuItemProps } from './use-menu-item.mjs';
import './base/menu-item-base.mjs';
import '@heroui/theme';
import '@react-aria/menu';
import '@react-types/shared';
import '@heroui/aria-utils';
import 'react';
import 'tailwind-variants';
import '@heroui/system';
import '@react-stately/tree';

interface MenuItemProps<T extends object = object> extends Omit<UseMenuItemProps<T>, "hasDescriptionTextChild" | "hasTitleTextChild"> {
}
/**
 * @internal
 */
declare const MenuItem: {
    (props: MenuItemProps): react_jsx_runtime.JSX.Element;
    displayName: string;
};

export { type MenuItemProps, MenuItem as default };

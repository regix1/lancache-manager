import * as _heroui_system from '@heroui/system';
import { Node } from '@react-types/shared';
import { TreeState } from '@react-stately/tree';
import { Key } from 'react';
import { MenuItemProps } from './menu-item.js';
import { MenuSectionBaseProps } from './base/menu-section-base.js';
import 'react/jsx-runtime';
import './use-menu-item.js';
import './base/menu-item-base.js';
import '@heroui/theme';
import '@react-aria/menu';
import '@heroui/aria-utils';
import 'tailwind-variants';
import '@heroui/divider';

interface MenuSectionProps<T extends object = object> extends MenuSectionBaseProps {
    item: Node<T>;
    state: TreeState<T>;
    /**
     * The menu items variant.
     */
    variant?: MenuItemProps["variant"];
    /**
     * The menu items color.
     */
    color?: MenuItemProps["color"];
    /**
     * Whether to disable the items animation.
     * @default false
     */
    disableAnimation?: boolean;
    /**
     * Whether the menu should close when the menu item is selected.
     * @default true
     */
    closeOnSelect?: MenuItemProps["closeOnSelect"];
    /**
     * Callback fired when the menu item is selected.
     */
    onAction?: (key: Key) => void;
}
/**
 * @internal
 */
declare const MenuSection: _heroui_system.InternalForwardRefRenderFunction<"li", MenuSectionProps<object>, never>;

export { type MenuSectionProps, MenuSection as default };

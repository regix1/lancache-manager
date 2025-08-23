import { SlotsToClasses, MenuSectionSlots } from '@heroui/theme';
import { SectionProps } from '@heroui/aria-utils';
import { DividerProps } from '@heroui/divider';
import { MenuItemProps } from '../menu-item.mjs';
import 'react/jsx-runtime';
import '../use-menu-item.mjs';
import './menu-item-base.mjs';
import '@react-aria/menu';
import '@react-types/shared';
import 'react';
import 'tailwind-variants';
import '@heroui/system';
import '@react-stately/tree';

interface MenuSectionBaseProps<T extends object = {}> extends SectionProps<"ul", T> {
    /**
     * The menu section classNames.
     */
    classNames?: SlotsToClasses<MenuSectionSlots>;
    /**
     * The menu items classNames.
     */
    itemClasses?: MenuItemProps["classNames"];
    /**
     * Shows a divider between sections
     * @default false
     */
    showDivider?: boolean;
    /**
     * Whether to hide the check icon when the items are selected.
     * @default false
     */
    hideSelectedIcon?: boolean;
    /**
     * The divider props
     */
    dividerProps?: DividerProps;
}
declare const MenuSectionBase: <T extends object>(props: MenuSectionBaseProps<T>) => JSX.Element;

export { type MenuSectionBaseProps, MenuSectionBase as default };

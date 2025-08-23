import { SlotsToClasses, ListboxSectionSlots } from '@heroui/theme';
import { SectionProps } from '@heroui/aria-utils';
import { DividerProps } from '@heroui/divider';
import { ListboxItemProps } from '../listbox-item.mjs';
import 'react/jsx-runtime';
import '../use-listbox-item.mjs';
import './listbox-item-base.mjs';
import '@react-aria/listbox';
import '@react-types/shared';
import 'react';
import 'tailwind-variants';
import '@heroui/system';
import '@react-stately/list';

interface ListboxSectionBaseProps<T extends object = {}> extends SectionProps<"ul", T> {
    /**
     * The listbox section classNames.
     */
    classNames?: SlotsToClasses<ListboxSectionSlots>;
    /**
     * The listbox items classNames.
     */
    itemClasses?: ListboxItemProps["classNames"];
    /**
     * Whether to hide the check icon when the items are selected.
     * @default false
     */
    hideSelectedIcon?: boolean;
    /**
     * Shows a divider between sections
     * @default false
     */
    showDivider?: boolean;
    /**
     * The divider props
     */
    dividerProps?: DividerProps;
}
declare const ListboxSectionBase: <T extends object>(props: ListboxSectionBaseProps<T>) => JSX.Element;

export { type ListboxSectionBaseProps, ListboxSectionBase as default };

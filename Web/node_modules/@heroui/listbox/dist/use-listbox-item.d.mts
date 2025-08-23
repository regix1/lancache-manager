import { ListboxItemBaseProps, ListboxItemSelectedIconProps } from './base/listbox-item-base.mjs';
import * as _heroui_theme from '@heroui/theme';
import { MenuItemVariantProps } from '@heroui/theme';
import * as tailwind_variants from 'tailwind-variants';
import * as react from 'react';
import * as _heroui_system from '@heroui/system';
import { HTMLHeroUIProps, PropGetter } from '@heroui/system';
import { Node } from '@react-types/shared';
import { ListState } from '@react-stately/list';
import '@react-aria/listbox';
import '@heroui/aria-utils';

interface Props<T extends object> extends ListboxItemBaseProps<T> {
    item: Node<T>;
    state: ListState<T>;
}
type UseListboxItemProps<T extends object> = Props<T> & Omit<HTMLHeroUIProps<"li">, keyof Props<T>> & MenuItemVariantProps;
declare function useListboxItem<T extends object>(originalProps: UseListboxItemProps<T>): {
    Component: _heroui_system.As<any>;
    domRef: react.RefObject<HTMLLIElement>;
    slots: {
        base: (slotProps?: ({
            color?: "default" | "primary" | "secondary" | "success" | "warning" | "danger" | undefined;
            disableAnimation?: boolean | undefined;
            isDisabled?: boolean | undefined;
            variant?: "flat" | "shadow" | "solid" | "bordered" | "faded" | "light" | undefined;
            showDivider?: boolean | undefined;
            hasTitleTextChild?: boolean | undefined;
            hasDescriptionTextChild?: boolean | undefined;
        } & tailwind_variants.ClassProp<tailwind_variants.ClassValue>) | undefined) => string;
        wrapper: (slotProps?: ({
            color?: "default" | "primary" | "secondary" | "success" | "warning" | "danger" | undefined;
            disableAnimation?: boolean | undefined;
            isDisabled?: boolean | undefined;
            variant?: "flat" | "shadow" | "solid" | "bordered" | "faded" | "light" | undefined;
            showDivider?: boolean | undefined;
            hasTitleTextChild?: boolean | undefined;
            hasDescriptionTextChild?: boolean | undefined;
        } & tailwind_variants.ClassProp<tailwind_variants.ClassValue>) | undefined) => string;
        title: (slotProps?: ({
            color?: "default" | "primary" | "secondary" | "success" | "warning" | "danger" | undefined;
            disableAnimation?: boolean | undefined;
            isDisabled?: boolean | undefined;
            variant?: "flat" | "shadow" | "solid" | "bordered" | "faded" | "light" | undefined;
            showDivider?: boolean | undefined;
            hasTitleTextChild?: boolean | undefined;
            hasDescriptionTextChild?: boolean | undefined;
        } & tailwind_variants.ClassProp<tailwind_variants.ClassValue>) | undefined) => string;
        description: (slotProps?: ({
            color?: "default" | "primary" | "secondary" | "success" | "warning" | "danger" | undefined;
            disableAnimation?: boolean | undefined;
            isDisabled?: boolean | undefined;
            variant?: "flat" | "shadow" | "solid" | "bordered" | "faded" | "light" | undefined;
            showDivider?: boolean | undefined;
            hasTitleTextChild?: boolean | undefined;
            hasDescriptionTextChild?: boolean | undefined;
        } & tailwind_variants.ClassProp<tailwind_variants.ClassValue>) | undefined) => string;
        selectedIcon: (slotProps?: ({
            color?: "default" | "primary" | "secondary" | "success" | "warning" | "danger" | undefined;
            disableAnimation?: boolean | undefined;
            isDisabled?: boolean | undefined;
            variant?: "flat" | "shadow" | "solid" | "bordered" | "faded" | "light" | undefined;
            showDivider?: boolean | undefined;
            hasTitleTextChild?: boolean | undefined;
            hasDescriptionTextChild?: boolean | undefined;
        } & tailwind_variants.ClassProp<tailwind_variants.ClassValue>) | undefined) => string;
        shortcut: (slotProps?: ({
            color?: "default" | "primary" | "secondary" | "success" | "warning" | "danger" | undefined;
            disableAnimation?: boolean | undefined;
            isDisabled?: boolean | undefined;
            variant?: "flat" | "shadow" | "solid" | "bordered" | "faded" | "light" | undefined;
            showDivider?: boolean | undefined;
            hasTitleTextChild?: boolean | undefined;
            hasDescriptionTextChild?: boolean | undefined;
        } & tailwind_variants.ClassProp<tailwind_variants.ClassValue>) | undefined) => string;
    } & {
        base: (slotProps?: ({
            color?: "default" | "primary" | "secondary" | "success" | "warning" | "danger" | undefined;
            disableAnimation?: boolean | undefined;
            isDisabled?: boolean | undefined;
            variant?: "flat" | "shadow" | "solid" | "bordered" | "faded" | "light" | undefined;
            showDivider?: boolean | undefined;
            hasTitleTextChild?: boolean | undefined;
            hasDescriptionTextChild?: boolean | undefined;
        } & tailwind_variants.ClassProp<tailwind_variants.ClassValue>) | undefined) => string;
        wrapper: (slotProps?: ({
            color?: "default" | "primary" | "secondary" | "success" | "warning" | "danger" | undefined;
            disableAnimation?: boolean | undefined;
            isDisabled?: boolean | undefined;
            variant?: "flat" | "shadow" | "solid" | "bordered" | "faded" | "light" | undefined;
            showDivider?: boolean | undefined;
            hasTitleTextChild?: boolean | undefined;
            hasDescriptionTextChild?: boolean | undefined;
        } & tailwind_variants.ClassProp<tailwind_variants.ClassValue>) | undefined) => string;
        title: (slotProps?: ({
            color?: "default" | "primary" | "secondary" | "success" | "warning" | "danger" | undefined;
            disableAnimation?: boolean | undefined;
            isDisabled?: boolean | undefined;
            variant?: "flat" | "shadow" | "solid" | "bordered" | "faded" | "light" | undefined;
            showDivider?: boolean | undefined;
            hasTitleTextChild?: boolean | undefined;
            hasDescriptionTextChild?: boolean | undefined;
        } & tailwind_variants.ClassProp<tailwind_variants.ClassValue>) | undefined) => string;
        description: (slotProps?: ({
            color?: "default" | "primary" | "secondary" | "success" | "warning" | "danger" | undefined;
            disableAnimation?: boolean | undefined;
            isDisabled?: boolean | undefined;
            variant?: "flat" | "shadow" | "solid" | "bordered" | "faded" | "light" | undefined;
            showDivider?: boolean | undefined;
            hasTitleTextChild?: boolean | undefined;
            hasDescriptionTextChild?: boolean | undefined;
        } & tailwind_variants.ClassProp<tailwind_variants.ClassValue>) | undefined) => string;
        selectedIcon: (slotProps?: ({
            color?: "default" | "primary" | "secondary" | "success" | "warning" | "danger" | undefined;
            disableAnimation?: boolean | undefined;
            isDisabled?: boolean | undefined;
            variant?: "flat" | "shadow" | "solid" | "bordered" | "faded" | "light" | undefined;
            showDivider?: boolean | undefined;
            hasTitleTextChild?: boolean | undefined;
            hasDescriptionTextChild?: boolean | undefined;
        } & tailwind_variants.ClassProp<tailwind_variants.ClassValue>) | undefined) => string;
        shortcut: (slotProps?: ({
            color?: "default" | "primary" | "secondary" | "success" | "warning" | "danger" | undefined;
            disableAnimation?: boolean | undefined;
            isDisabled?: boolean | undefined;
            variant?: "flat" | "shadow" | "solid" | "bordered" | "faded" | "light" | undefined;
            showDivider?: boolean | undefined;
            hasTitleTextChild?: boolean | undefined;
            hasDescriptionTextChild?: boolean | undefined;
        } & tailwind_variants.ClassProp<tailwind_variants.ClassValue>) | undefined) => string;
    } & {};
    classNames: _heroui_theme.SlotsToClasses<"base" | "title" | "description" | "wrapper" | "selectedIcon" | "shortcut"> | undefined;
    isSelectable: boolean;
    isSelected: boolean;
    isDisabled: boolean | undefined;
    rendered: react.ReactNode;
    description: react.ReactNode;
    startContent: react.ReactNode;
    endContent: react.ReactNode;
    selectedIcon: react.ReactNode | ((props: ListboxItemSelectedIconProps) => react.ReactNode);
    hideSelectedIcon: boolean;
    disableAnimation: boolean;
    getItemProps: PropGetter;
    getLabelProps: PropGetter;
    getWrapperProps: PropGetter;
    getDescriptionProps: PropGetter;
    getSelectedIconProps: PropGetter;
};
type UseListboxItemReturn = ReturnType<typeof useListboxItem>;

export { type UseListboxItemProps, type UseListboxItemReturn, useListboxItem };

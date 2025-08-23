import { PopoverProps } from '@heroui/popover';
import { MenuTriggerType } from '@react-types/menu';
import { Ref } from 'react';
import { PropGetter, HTMLHeroUIProps } from '@heroui/system';
import { ReactRef } from '@heroui/react-utils';
import { MenuProps } from '@heroui/menu';

interface Props extends HTMLHeroUIProps<"div"> {
    /**
     * Type of overlay that is opened by the trigger.
     */
    type?: "menu" | "listbox";
    /**
     * Ref to the DOM node.
     */
    ref?: ReactRef<HTMLElement | null>;
    /**
     * How the menu is triggered.
     * @default 'press'
     */
    trigger?: MenuTriggerType;
    /**
     * Whether menu trigger is disabled.
     * @default false
     */
    isDisabled?: boolean;
    /**
     * Whether the Menu closes when a selection is made.
     * @default true
     */
    closeOnSelect?: boolean;
}
type UseDropdownProps = Props & Omit<PopoverProps, "children" | "color" | "variant">;
declare function useDropdown(props: UseDropdownProps): UseDropdownReturn;
type UseDropdownReturn = {
    Component: string | React.ElementType;
    menuRef: React.RefObject<HTMLUListElement>;
    menuProps: any;
    closeOnSelect: boolean;
    onClose: () => void;
    autoFocus: any;
    disableAnimation: boolean;
    getPopoverProps: PropGetter;
    getMenuProps: <T extends object>(props?: Partial<MenuProps<T>>, ref?: Ref<any>) => MenuProps;
    getMenuTriggerProps: (props?: any) => any;
};

export { type UseDropdownProps, type UseDropdownReturn, useDropdown };

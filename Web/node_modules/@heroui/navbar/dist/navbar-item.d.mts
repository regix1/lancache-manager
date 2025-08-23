import * as _heroui_system from '@heroui/system';
import { HTMLHeroUIProps } from '@heroui/system';

interface NavbarItemProps extends HTMLHeroUIProps<"li"> {
    children?: React.ReactNode;
    /**
     * Whether the item is active or not.
     * @default false
     */
    isActive?: boolean;
}
declare const NavbarItem: _heroui_system.InternalForwardRefRenderFunction<"li", NavbarItemProps, never>;

export { type NavbarItemProps, NavbarItem as default };

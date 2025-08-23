import * as react from 'react';
import { ReactNode } from 'react';
import * as tailwind_variants from 'tailwind-variants';
import * as _heroui_system from '@heroui/system';
import { HTMLHeroUIProps, PropGetter } from '@heroui/system';
import { ChipVariantProps, SlotsToClasses, ChipSlots } from '@heroui/theme';
import { ReactRef } from '@heroui/react-utils';
import { PressEvent } from '@react-types/shared';

interface UseChipProps extends HTMLHeroUIProps, ChipVariantProps {
    /**
     * Ref to the DOM node.
     */
    ref?: ReactRef<HTMLDivElement | null>;
    /**
     * Avatar to be rendered in the left side of the chip.
     */
    avatar?: React.ReactNode;
    /**
     * Element to be rendered in the left side of the chip.
     * this props overrides the `avatar` prop.
     */
    startContent?: React.ReactNode;
    /**
     * Element to be rendered in the right side of the chip.
     * if you pass this prop and the `onClose` prop, the passed element
     * will have the close button props and it will be rendered instead of the
     * default close button.
     */
    endContent?: React.ReactNode;
    /**
     * Classname or List of classes to change the classNames of the element.
     * if `className` is passed, it will be added to the base slot.
     *
     * @example
     * ```ts
     * <Chip classNames={{
     *    base:"base-classes",
     *    dot: "dot-classes",
     *    content: "content-classes",
     *    avatar: "avatar-classes",
     *    closeButton: "close-button-classes",
     * }} />
     * ```
     */
    classNames?: SlotsToClasses<ChipSlots>;
    /**
     * Callback fired when the chip is closed. if you pass this prop,
     * the chip will display a close button (endContent).
     * @param e PressEvent
     */
    onClose?: (e: PressEvent) => void;
}
declare function useChip(originalProps: UseChipProps): {
    Component: _heroui_system.As<any>;
    children: ReactNode;
    slots: {
        base: (slotProps?: ({
            isOneChar?: boolean | undefined;
            isCloseButtonFocusVisible?: boolean | undefined;
            color?: "default" | "primary" | "secondary" | "success" | "warning" | "danger" | undefined;
            size?: "md" | "sm" | "lg" | undefined;
            variant?: "dot" | "solid" | "flat" | "shadow" | "bordered" | "faded" | "light" | undefined;
            radius?: "md" | "full" | "sm" | "lg" | "none" | undefined;
            isDisabled?: boolean | undefined;
            hasStartContent?: boolean | undefined;
            hasEndContent?: boolean | undefined;
            isCloseable?: boolean | undefined;
        } & tailwind_variants.ClassProp<tailwind_variants.ClassValue>) | undefined) => string;
        content: (slotProps?: ({
            isOneChar?: boolean | undefined;
            isCloseButtonFocusVisible?: boolean | undefined;
            color?: "default" | "primary" | "secondary" | "success" | "warning" | "danger" | undefined;
            size?: "md" | "sm" | "lg" | undefined;
            variant?: "dot" | "solid" | "flat" | "shadow" | "bordered" | "faded" | "light" | undefined;
            radius?: "md" | "full" | "sm" | "lg" | "none" | undefined;
            isDisabled?: boolean | undefined;
            hasStartContent?: boolean | undefined;
            hasEndContent?: boolean | undefined;
            isCloseable?: boolean | undefined;
        } & tailwind_variants.ClassProp<tailwind_variants.ClassValue>) | undefined) => string;
        dot: (slotProps?: ({
            isOneChar?: boolean | undefined;
            isCloseButtonFocusVisible?: boolean | undefined;
            color?: "default" | "primary" | "secondary" | "success" | "warning" | "danger" | undefined;
            size?: "md" | "sm" | "lg" | undefined;
            variant?: "dot" | "solid" | "flat" | "shadow" | "bordered" | "faded" | "light" | undefined;
            radius?: "md" | "full" | "sm" | "lg" | "none" | undefined;
            isDisabled?: boolean | undefined;
            hasStartContent?: boolean | undefined;
            hasEndContent?: boolean | undefined;
            isCloseable?: boolean | undefined;
        } & tailwind_variants.ClassProp<tailwind_variants.ClassValue>) | undefined) => string;
        avatar: (slotProps?: ({
            isOneChar?: boolean | undefined;
            isCloseButtonFocusVisible?: boolean | undefined;
            color?: "default" | "primary" | "secondary" | "success" | "warning" | "danger" | undefined;
            size?: "md" | "sm" | "lg" | undefined;
            variant?: "dot" | "solid" | "flat" | "shadow" | "bordered" | "faded" | "light" | undefined;
            radius?: "md" | "full" | "sm" | "lg" | "none" | undefined;
            isDisabled?: boolean | undefined;
            hasStartContent?: boolean | undefined;
            hasEndContent?: boolean | undefined;
            isCloseable?: boolean | undefined;
        } & tailwind_variants.ClassProp<tailwind_variants.ClassValue>) | undefined) => string;
        closeButton: (slotProps?: ({
            isOneChar?: boolean | undefined;
            isCloseButtonFocusVisible?: boolean | undefined;
            color?: "default" | "primary" | "secondary" | "success" | "warning" | "danger" | undefined;
            size?: "md" | "sm" | "lg" | undefined;
            variant?: "dot" | "solid" | "flat" | "shadow" | "bordered" | "faded" | "light" | undefined;
            radius?: "md" | "full" | "sm" | "lg" | "none" | undefined;
            isDisabled?: boolean | undefined;
            hasStartContent?: boolean | undefined;
            hasEndContent?: boolean | undefined;
            isCloseable?: boolean | undefined;
        } & tailwind_variants.ClassProp<tailwind_variants.ClassValue>) | undefined) => string;
    } & {
        base: (slotProps?: ({
            isOneChar?: boolean | undefined;
            isCloseButtonFocusVisible?: boolean | undefined;
            color?: "default" | "primary" | "secondary" | "success" | "warning" | "danger" | undefined;
            size?: "md" | "sm" | "lg" | undefined;
            variant?: "dot" | "solid" | "flat" | "shadow" | "bordered" | "faded" | "light" | undefined;
            radius?: "md" | "full" | "sm" | "lg" | "none" | undefined;
            isDisabled?: boolean | undefined;
            hasStartContent?: boolean | undefined;
            hasEndContent?: boolean | undefined;
            isCloseable?: boolean | undefined;
        } & tailwind_variants.ClassProp<tailwind_variants.ClassValue>) | undefined) => string;
        content: (slotProps?: ({
            isOneChar?: boolean | undefined;
            isCloseButtonFocusVisible?: boolean | undefined;
            color?: "default" | "primary" | "secondary" | "success" | "warning" | "danger" | undefined;
            size?: "md" | "sm" | "lg" | undefined;
            variant?: "dot" | "solid" | "flat" | "shadow" | "bordered" | "faded" | "light" | undefined;
            radius?: "md" | "full" | "sm" | "lg" | "none" | undefined;
            isDisabled?: boolean | undefined;
            hasStartContent?: boolean | undefined;
            hasEndContent?: boolean | undefined;
            isCloseable?: boolean | undefined;
        } & tailwind_variants.ClassProp<tailwind_variants.ClassValue>) | undefined) => string;
        dot: (slotProps?: ({
            isOneChar?: boolean | undefined;
            isCloseButtonFocusVisible?: boolean | undefined;
            color?: "default" | "primary" | "secondary" | "success" | "warning" | "danger" | undefined;
            size?: "md" | "sm" | "lg" | undefined;
            variant?: "dot" | "solid" | "flat" | "shadow" | "bordered" | "faded" | "light" | undefined;
            radius?: "md" | "full" | "sm" | "lg" | "none" | undefined;
            isDisabled?: boolean | undefined;
            hasStartContent?: boolean | undefined;
            hasEndContent?: boolean | undefined;
            isCloseable?: boolean | undefined;
        } & tailwind_variants.ClassProp<tailwind_variants.ClassValue>) | undefined) => string;
        avatar: (slotProps?: ({
            isOneChar?: boolean | undefined;
            isCloseButtonFocusVisible?: boolean | undefined;
            color?: "default" | "primary" | "secondary" | "success" | "warning" | "danger" | undefined;
            size?: "md" | "sm" | "lg" | undefined;
            variant?: "dot" | "solid" | "flat" | "shadow" | "bordered" | "faded" | "light" | undefined;
            radius?: "md" | "full" | "sm" | "lg" | "none" | undefined;
            isDisabled?: boolean | undefined;
            hasStartContent?: boolean | undefined;
            hasEndContent?: boolean | undefined;
            isCloseable?: boolean | undefined;
        } & tailwind_variants.ClassProp<tailwind_variants.ClassValue>) | undefined) => string;
        closeButton: (slotProps?: ({
            isOneChar?: boolean | undefined;
            isCloseButtonFocusVisible?: boolean | undefined;
            color?: "default" | "primary" | "secondary" | "success" | "warning" | "danger" | undefined;
            size?: "md" | "sm" | "lg" | undefined;
            variant?: "dot" | "solid" | "flat" | "shadow" | "bordered" | "faded" | "light" | undefined;
            radius?: "md" | "full" | "sm" | "lg" | "none" | undefined;
            isDisabled?: boolean | undefined;
            hasStartContent?: boolean | undefined;
            hasEndContent?: boolean | undefined;
            isCloseable?: boolean | undefined;
        } & tailwind_variants.ClassProp<tailwind_variants.ClassValue>) | undefined) => string;
    } & {};
    classNames: SlotsToClasses<"base" | "content" | "avatar" | "dot" | "closeButton"> | undefined;
    isDot: boolean;
    isCloseable: boolean;
    startContent: react.DetailedReactHTMLElement<react.HTMLAttributes<HTMLElement>, HTMLElement> | null;
    endContent: react.DetailedReactHTMLElement<react.HTMLAttributes<HTMLElement>, HTMLElement> | null;
    getCloseButtonProps: PropGetter;
    getChipProps: PropGetter;
};
type UseChipReturn = ReturnType<typeof useChip>;

export { type UseChipProps, type UseChipReturn, useChip };

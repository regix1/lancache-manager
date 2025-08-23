import * as tailwind_variants from 'tailwind-variants';
import * as react from 'react';
import * as _heroui_system from '@heroui/system';
import { HTMLHeroUIProps, PropGetter } from '@heroui/system';
import { SlotsToClasses, CircularProgressSlots, CircularProgressVariantProps } from '@heroui/theme';
import { AriaProgressBarProps } from '@react-types/progress';
import { ReactRef } from '@heroui/react-utils';

interface Props extends HTMLHeroUIProps<"div"> {
    /**
     * Ref to the DOM node.
     */
    ref?: ReactRef<HTMLElement | null>;
    /**
     * The stroke of the circle and tracker
     * @default 2
     */
    strokeWidth?: number;
    /**
     * Whether to show the value label.
     * @default false
     */
    showValueLabel?: boolean;
    /**
     * Classname or List of classes to change the classNames of the element.
     * if `className` is passed, it will be added to the base slot.
     *
     * @example
     * ```ts
     * <CircularProgress classNames={{
     *    base:"base-classes",
     *    label: "label-classes",
     *    value: "value-classes",
     *    svg: "svg-classes", // the svg wrapper
     *    track: "track-classes", // the circle of the background
     *    indicator: "indicator-classes", // the circle of the progress
     * }} />
     * ```
     */
    classNames?: SlotsToClasses<CircularProgressSlots>;
}
type UseCircularProgressProps = Props & AriaProgressBarProps & CircularProgressVariantProps;
declare function useCircularProgress(originalProps: UseCircularProgressProps): {
    Component: _heroui_system.As<any>;
    domRef: react.RefObject<HTMLElement>;
    slots: {
        base: (slotProps?: ({
            color?: "primary" | "default" | "secondary" | "success" | "warning" | "danger" | undefined;
            size?: "md" | "sm" | "lg" | undefined;
            isDisabled?: boolean | undefined;
            disableAnimation?: boolean | undefined;
            isIndeterminate?: boolean | undefined;
        } & tailwind_variants.ClassProp<tailwind_variants.ClassValue>) | undefined) => string;
        label: (slotProps?: ({
            color?: "primary" | "default" | "secondary" | "success" | "warning" | "danger" | undefined;
            size?: "md" | "sm" | "lg" | undefined;
            isDisabled?: boolean | undefined;
            disableAnimation?: boolean | undefined;
            isIndeterminate?: boolean | undefined;
        } & tailwind_variants.ClassProp<tailwind_variants.ClassValue>) | undefined) => string;
        svgWrapper: (slotProps?: ({
            color?: "primary" | "default" | "secondary" | "success" | "warning" | "danger" | undefined;
            size?: "md" | "sm" | "lg" | undefined;
            isDisabled?: boolean | undefined;
            disableAnimation?: boolean | undefined;
            isIndeterminate?: boolean | undefined;
        } & tailwind_variants.ClassProp<tailwind_variants.ClassValue>) | undefined) => string;
        svg: (slotProps?: ({
            color?: "primary" | "default" | "secondary" | "success" | "warning" | "danger" | undefined;
            size?: "md" | "sm" | "lg" | undefined;
            isDisabled?: boolean | undefined;
            disableAnimation?: boolean | undefined;
            isIndeterminate?: boolean | undefined;
        } & tailwind_variants.ClassProp<tailwind_variants.ClassValue>) | undefined) => string;
        track: (slotProps?: ({
            color?: "primary" | "default" | "secondary" | "success" | "warning" | "danger" | undefined;
            size?: "md" | "sm" | "lg" | undefined;
            isDisabled?: boolean | undefined;
            disableAnimation?: boolean | undefined;
            isIndeterminate?: boolean | undefined;
        } & tailwind_variants.ClassProp<tailwind_variants.ClassValue>) | undefined) => string;
        indicator: (slotProps?: ({
            color?: "primary" | "default" | "secondary" | "success" | "warning" | "danger" | undefined;
            size?: "md" | "sm" | "lg" | undefined;
            isDisabled?: boolean | undefined;
            disableAnimation?: boolean | undefined;
            isIndeterminate?: boolean | undefined;
        } & tailwind_variants.ClassProp<tailwind_variants.ClassValue>) | undefined) => string;
        value: (slotProps?: ({
            color?: "primary" | "default" | "secondary" | "success" | "warning" | "danger" | undefined;
            size?: "md" | "sm" | "lg" | undefined;
            isDisabled?: boolean | undefined;
            disableAnimation?: boolean | undefined;
            isIndeterminate?: boolean | undefined;
        } & tailwind_variants.ClassProp<tailwind_variants.ClassValue>) | undefined) => string;
    } & {
        base: (slotProps?: ({
            color?: "primary" | "default" | "secondary" | "success" | "warning" | "danger" | undefined;
            size?: "md" | "sm" | "lg" | undefined;
            isDisabled?: boolean | undefined;
            disableAnimation?: boolean | undefined;
            isIndeterminate?: boolean | undefined;
        } & tailwind_variants.ClassProp<tailwind_variants.ClassValue>) | undefined) => string;
        label: (slotProps?: ({
            color?: "primary" | "default" | "secondary" | "success" | "warning" | "danger" | undefined;
            size?: "md" | "sm" | "lg" | undefined;
            isDisabled?: boolean | undefined;
            disableAnimation?: boolean | undefined;
            isIndeterminate?: boolean | undefined;
        } & tailwind_variants.ClassProp<tailwind_variants.ClassValue>) | undefined) => string;
        svgWrapper: (slotProps?: ({
            color?: "primary" | "default" | "secondary" | "success" | "warning" | "danger" | undefined;
            size?: "md" | "sm" | "lg" | undefined;
            isDisabled?: boolean | undefined;
            disableAnimation?: boolean | undefined;
            isIndeterminate?: boolean | undefined;
        } & tailwind_variants.ClassProp<tailwind_variants.ClassValue>) | undefined) => string;
        svg: (slotProps?: ({
            color?: "primary" | "default" | "secondary" | "success" | "warning" | "danger" | undefined;
            size?: "md" | "sm" | "lg" | undefined;
            isDisabled?: boolean | undefined;
            disableAnimation?: boolean | undefined;
            isIndeterminate?: boolean | undefined;
        } & tailwind_variants.ClassProp<tailwind_variants.ClassValue>) | undefined) => string;
        track: (slotProps?: ({
            color?: "primary" | "default" | "secondary" | "success" | "warning" | "danger" | undefined;
            size?: "md" | "sm" | "lg" | undefined;
            isDisabled?: boolean | undefined;
            disableAnimation?: boolean | undefined;
            isIndeterminate?: boolean | undefined;
        } & tailwind_variants.ClassProp<tailwind_variants.ClassValue>) | undefined) => string;
        indicator: (slotProps?: ({
            color?: "primary" | "default" | "secondary" | "success" | "warning" | "danger" | undefined;
            size?: "md" | "sm" | "lg" | undefined;
            isDisabled?: boolean | undefined;
            disableAnimation?: boolean | undefined;
            isIndeterminate?: boolean | undefined;
        } & tailwind_variants.ClassProp<tailwind_variants.ClassValue>) | undefined) => string;
        value: (slotProps?: ({
            color?: "primary" | "default" | "secondary" | "success" | "warning" | "danger" | undefined;
            size?: "md" | "sm" | "lg" | undefined;
            isDisabled?: boolean | undefined;
            disableAnimation?: boolean | undefined;
            isIndeterminate?: boolean | undefined;
        } & tailwind_variants.ClassProp<tailwind_variants.ClassValue>) | undefined) => string;
    } & {};
    classNames: SlotsToClasses<"base" | "label" | "svgWrapper" | "svg" | "track" | "indicator" | "value"> | undefined;
    label: react.ReactNode;
    showValueLabel: boolean;
    getProgressBarProps: PropGetter;
    getLabelProps: PropGetter;
    getSvgProps: PropGetter;
    getIndicatorProps: PropGetter;
    getTrackProps: PropGetter;
};
type UseCircularProgressReturn = ReturnType<typeof useCircularProgress>;

export { type Props, type UseCircularProgressProps, type UseCircularProgressReturn, useCircularProgress };

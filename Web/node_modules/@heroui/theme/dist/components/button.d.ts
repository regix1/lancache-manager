import * as tailwind_variants from 'tailwind-variants';
import { VariantProps } from 'tailwind-variants';

/**
 * Button wrapper **Tailwind Variants** component
 *
 * const classNames = button({...})
 *
 * @example
 * <button
 *  className={classNames())}
 *  data-pressed={true/false}
 *  data-hover={true/false}
 *  data-focus={true/false}
 *  data-focus-visible={true/false}
 * >
 *   Button
 * </button>
 */
declare const button: tailwind_variants.TVReturnType<{
    variant: {
        solid: string;
        bordered: string;
        light: string;
        flat: string;
        faded: string;
        shadow: string;
        ghost: string;
    };
    size: {
        sm: string;
        md: string;
        lg: string;
    };
    color: {
        default: string;
        primary: string;
        secondary: string;
        success: string;
        warning: string;
        danger: string;
    };
    radius: {
        none: string;
        sm: string;
        md: string;
        lg: string;
        full: string;
    };
    fullWidth: {
        true: string;
    };
    isDisabled: {
        true: string;
    };
    isInGroup: {
        true: string;
    };
    isIconOnly: {
        true: string;
        false: string;
    };
    disableAnimation: {
        true: string;
        false: string;
    };
}, undefined, string[], {
    variant: {
        solid: string;
        bordered: string;
        light: string;
        flat: string;
        faded: string;
        shadow: string;
        ghost: string;
    };
    size: {
        sm: string;
        md: string;
        lg: string;
    };
    color: {
        default: string;
        primary: string;
        secondary: string;
        success: string;
        warning: string;
        danger: string;
    };
    radius: {
        none: string;
        sm: string;
        md: string;
        lg: string;
        full: string;
    };
    fullWidth: {
        true: string;
    };
    isDisabled: {
        true: string;
    };
    isInGroup: {
        true: string;
    };
    isIconOnly: {
        true: string;
        false: string;
    };
    disableAnimation: {
        true: string;
        false: string;
    };
}, undefined, tailwind_variants.TVReturnType<{
    variant: {
        solid: string;
        bordered: string;
        light: string;
        flat: string;
        faded: string;
        shadow: string;
        ghost: string;
    };
    size: {
        sm: string;
        md: string;
        lg: string;
    };
    color: {
        default: string;
        primary: string;
        secondary: string;
        success: string;
        warning: string;
        danger: string;
    };
    radius: {
        none: string;
        sm: string;
        md: string;
        lg: string;
        full: string;
    };
    fullWidth: {
        true: string;
    };
    isDisabled: {
        true: string;
    };
    isInGroup: {
        true: string;
    };
    isIconOnly: {
        true: string;
        false: string;
    };
    disableAnimation: {
        true: string;
        false: string;
    };
}, undefined, string[], unknown, unknown, undefined>>;
/**
 * ButtonGroup wrapper **Tailwind Variants** component
 *
 * const classNames = buttonGroup({...})
 *
 * @example
 * <div role="group" className={classNames())}>
 *   // button elements
 * </div>
 */
declare const buttonGroup: tailwind_variants.TVReturnType<{
    fullWidth: {
        true: string;
    };
}, undefined, "inline-flex items-center justify-center h-auto", {
    fullWidth: {
        true: string;
    };
}, undefined, tailwind_variants.TVReturnType<{
    fullWidth: {
        true: string;
    };
}, undefined, "inline-flex items-center justify-center h-auto", unknown, unknown, undefined>>;
type ButtonGroupVariantProps = VariantProps<typeof buttonGroup>;
type ButtonVariantProps = VariantProps<typeof button>;

export { type ButtonGroupVariantProps, type ButtonVariantProps, button, buttonGroup };

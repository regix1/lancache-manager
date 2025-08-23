import * as tailwind_variants from 'tailwind-variants';
import { VariantProps } from 'tailwind-variants';

/**
 * Link wrapper **Tailwind Variants** component
 *
 * @example
 * <a className={link({ color: "secondary", isBlock: true })} href="#" />
 */
declare const link: tailwind_variants.TVReturnType<{
    size: {
        sm: string;
        md: string;
        lg: string;
    };
    color: {
        foreground: string;
        primary: string;
        secondary: string;
        success: string;
        warning: string;
        danger: string;
    };
    underline: {
        none: string;
        hover: string;
        always: string;
        active: string;
        focus: string;
    };
    isBlock: {
        true: string[];
        false: string;
    };
    isDisabled: {
        true: string;
    };
    disableAnimation: {
        true: string;
    };
}, undefined, string[], {
    size: {
        sm: string;
        md: string;
        lg: string;
    };
    color: {
        foreground: string;
        primary: string;
        secondary: string;
        success: string;
        warning: string;
        danger: string;
    };
    underline: {
        none: string;
        hover: string;
        always: string;
        active: string;
        focus: string;
    };
    isBlock: {
        true: string[];
        false: string;
    };
    isDisabled: {
        true: string;
    };
    disableAnimation: {
        true: string;
    };
}, undefined, tailwind_variants.TVReturnType<{
    size: {
        sm: string;
        md: string;
        lg: string;
    };
    color: {
        foreground: string;
        primary: string;
        secondary: string;
        success: string;
        warning: string;
        danger: string;
    };
    underline: {
        none: string;
        hover: string;
        always: string;
        active: string;
        focus: string;
    };
    isBlock: {
        true: string[];
        false: string;
    };
    isDisabled: {
        true: string;
    };
    disableAnimation: {
        true: string;
    };
}, undefined, string[], unknown, unknown, undefined>>;
declare const linkAnchorClasses = "flex mx-1 text-current self-center";
type LinkVariantProps = VariantProps<typeof link>;

export { type LinkVariantProps, link, linkAnchorClasses };

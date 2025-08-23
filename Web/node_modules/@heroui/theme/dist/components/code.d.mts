import * as tailwind_variants from 'tailwind-variants';
import { VariantProps } from 'tailwind-variants';

/**
 * Code wrapper **Tailwind Variants** component
 *
 * const classNames = code({...})
 *
 * @example
 * <code className={classNames)}>
 *   npm install @heroui/react
 * </code>
 */
declare const code: tailwind_variants.TVReturnType<{
    color: {
        default: string;
        primary: string;
        secondary: string;
        success: string;
        warning: string;
        danger: string;
    };
    size: {
        sm: string;
        md: string;
        lg: string;
    };
    radius: {
        none: string;
        sm: string;
        md: string;
        lg: string;
        full: string;
    };
}, undefined, string[], {
    color: {
        default: string;
        primary: string;
        secondary: string;
        success: string;
        warning: string;
        danger: string;
    };
    size: {
        sm: string;
        md: string;
        lg: string;
    };
    radius: {
        none: string;
        sm: string;
        md: string;
        lg: string;
        full: string;
    };
}, undefined, tailwind_variants.TVReturnType<{
    color: {
        default: string;
        primary: string;
        secondary: string;
        success: string;
        warning: string;
        danger: string;
    };
    size: {
        sm: string;
        md: string;
        lg: string;
    };
    radius: {
        none: string;
        sm: string;
        md: string;
        lg: string;
        full: string;
    };
}, undefined, string[], unknown, unknown, undefined>>;
type CodeVariantProps = VariantProps<typeof code>;

export { type CodeVariantProps, code };

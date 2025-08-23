import * as tailwind_variants from 'tailwind-variants';
import { VariantProps } from 'tailwind-variants';

/**
 * Spinner wrapper **Tailwind Variants** component
 *
 * const {base, circle1, circle2, label } = spinner({...})
 *
 * @example
 * <div className={base())}>
 *    <i className={circle1()}/>
 *    <i className={circle2()}/>
 *    <span className={label()}/>
 * </div>
 */
declare const spinner: tailwind_variants.TVReturnType<{
    size: {
        sm: {
            wrapper: string;
            circle1: string;
            circle2: string;
            dots: string;
            label: string;
        };
        md: {
            wrapper: string;
            circle1: string;
            circle2: string;
            dots: string;
            label: string;
        };
        lg: {
            wrapper: string;
            circle1: string;
            circle2: string;
            dots: string;
            label: string;
        };
    };
    color: {
        current: {
            circle1: string;
            circle2: string;
            dots: string;
            spinnerBars: string;
        };
        white: {
            circle1: string;
            circle2: string;
            dots: string;
            spinnerBars: string;
        };
        default: {
            circle1: string;
            circle2: string;
            dots: string;
            spinnerBars: string;
        };
        primary: {
            circle1: string;
            circle2: string;
            dots: string;
            spinnerBars: string;
        };
        secondary: {
            circle1: string;
            circle2: string;
            dots: string;
            spinnerBars: string;
        };
        success: {
            circle1: string;
            circle2: string;
            dots: string;
            spinnerBars: string;
        };
        warning: {
            circle1: string;
            circle2: string;
            dots: string;
            spinnerBars: string;
        };
        danger: {
            circle1: string;
            circle2: string;
            dots: string;
            spinnerBars: string;
        };
    };
    labelColor: {
        foreground: {
            label: string;
        };
        primary: {
            label: string;
        };
        secondary: {
            label: string;
        };
        success: {
            label: string;
        };
        warning: {
            label: string;
        };
        danger: {
            label: string;
        };
    };
    variant: {
        default: {
            circle1: string[];
            circle2: string[];
        };
        gradient: {
            circle1: string[];
            circle2: string[];
        };
        wave: {
            wrapper: string;
            dots: string[];
        };
        dots: {
            wrapper: string;
            dots: string[];
        };
        spinner: {};
        simple: {
            wrapper: string;
            circle1: string;
            circle2: string;
        };
    };
}, {
    base: string;
    wrapper: string;
    label: string;
    circle1: string;
    circle2: string;
    dots: string;
    spinnerBars: string[];
}, undefined, {
    size: {
        sm: {
            wrapper: string;
            circle1: string;
            circle2: string;
            dots: string;
            label: string;
        };
        md: {
            wrapper: string;
            circle1: string;
            circle2: string;
            dots: string;
            label: string;
        };
        lg: {
            wrapper: string;
            circle1: string;
            circle2: string;
            dots: string;
            label: string;
        };
    };
    color: {
        current: {
            circle1: string;
            circle2: string;
            dots: string;
            spinnerBars: string;
        };
        white: {
            circle1: string;
            circle2: string;
            dots: string;
            spinnerBars: string;
        };
        default: {
            circle1: string;
            circle2: string;
            dots: string;
            spinnerBars: string;
        };
        primary: {
            circle1: string;
            circle2: string;
            dots: string;
            spinnerBars: string;
        };
        secondary: {
            circle1: string;
            circle2: string;
            dots: string;
            spinnerBars: string;
        };
        success: {
            circle1: string;
            circle2: string;
            dots: string;
            spinnerBars: string;
        };
        warning: {
            circle1: string;
            circle2: string;
            dots: string;
            spinnerBars: string;
        };
        danger: {
            circle1: string;
            circle2: string;
            dots: string;
            spinnerBars: string;
        };
    };
    labelColor: {
        foreground: {
            label: string;
        };
        primary: {
            label: string;
        };
        secondary: {
            label: string;
        };
        success: {
            label: string;
        };
        warning: {
            label: string;
        };
        danger: {
            label: string;
        };
    };
    variant: {
        default: {
            circle1: string[];
            circle2: string[];
        };
        gradient: {
            circle1: string[];
            circle2: string[];
        };
        wave: {
            wrapper: string;
            dots: string[];
        };
        dots: {
            wrapper: string;
            dots: string[];
        };
        spinner: {};
        simple: {
            wrapper: string;
            circle1: string;
            circle2: string;
        };
    };
}, {
    base: string;
    wrapper: string;
    label: string;
    circle1: string;
    circle2: string;
    dots: string;
    spinnerBars: string[];
}, tailwind_variants.TVReturnType<{
    size: {
        sm: {
            wrapper: string;
            circle1: string;
            circle2: string;
            dots: string;
            label: string;
        };
        md: {
            wrapper: string;
            circle1: string;
            circle2: string;
            dots: string;
            label: string;
        };
        lg: {
            wrapper: string;
            circle1: string;
            circle2: string;
            dots: string;
            label: string;
        };
    };
    color: {
        current: {
            circle1: string;
            circle2: string;
            dots: string;
            spinnerBars: string;
        };
        white: {
            circle1: string;
            circle2: string;
            dots: string;
            spinnerBars: string;
        };
        default: {
            circle1: string;
            circle2: string;
            dots: string;
            spinnerBars: string;
        };
        primary: {
            circle1: string;
            circle2: string;
            dots: string;
            spinnerBars: string;
        };
        secondary: {
            circle1: string;
            circle2: string;
            dots: string;
            spinnerBars: string;
        };
        success: {
            circle1: string;
            circle2: string;
            dots: string;
            spinnerBars: string;
        };
        warning: {
            circle1: string;
            circle2: string;
            dots: string;
            spinnerBars: string;
        };
        danger: {
            circle1: string;
            circle2: string;
            dots: string;
            spinnerBars: string;
        };
    };
    labelColor: {
        foreground: {
            label: string;
        };
        primary: {
            label: string;
        };
        secondary: {
            label: string;
        };
        success: {
            label: string;
        };
        warning: {
            label: string;
        };
        danger: {
            label: string;
        };
    };
    variant: {
        default: {
            circle1: string[];
            circle2: string[];
        };
        gradient: {
            circle1: string[];
            circle2: string[];
        };
        wave: {
            wrapper: string;
            dots: string[];
        };
        dots: {
            wrapper: string;
            dots: string[];
        };
        spinner: {};
        simple: {
            wrapper: string;
            circle1: string;
            circle2: string;
        };
    };
}, {
    base: string;
    wrapper: string;
    label: string;
    circle1: string;
    circle2: string;
    dots: string;
    spinnerBars: string[];
}, undefined, unknown, unknown, undefined>>;
type SpinnerVariantProps = VariantProps<typeof spinner>;
type SpinnerSlots = keyof ReturnType<typeof spinner>;

export { type SpinnerSlots, type SpinnerVariantProps, spinner };

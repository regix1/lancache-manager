import * as tailwind_variants from 'tailwind-variants';
import { VariantProps } from 'tailwind-variants';

/**
 * Badge wrapper **Tailwind Variants** component
 *
 * const {base, badge} = badge({...})
 *
 * @example
 * <div className={base())}>
 *   // children
 *   <span className={badge()}>5+</span>
 * </div>
 */
declare const badge: tailwind_variants.TVReturnType<{
    variant: {
        solid: {};
        flat: {};
        faded: {
            badge: string;
        };
        shadow: {};
    };
    color: {
        default: {};
        primary: {};
        secondary: {};
        success: {};
        warning: {};
        danger: {};
    };
    size: {
        sm: {
            badge: string;
        };
        md: {
            badge: string;
        };
        lg: {
            badge: string;
        };
    };
    placement: {
        "top-right": {};
        "top-left": {};
        "bottom-right": {};
        "bottom-left": {};
    };
    shape: {
        circle: {};
        rectangle: {};
    };
    isInvisible: {
        true: {};
    };
    isOneChar: {
        true: {
            badge: string;
        };
    };
    isDot: {
        true: {};
    };
    disableAnimation: {
        true: {
            badge: string;
        };
        false: {
            badge: string;
        };
    };
    showOutline: {
        true: {
            badge: string;
        };
        false: {
            badge: string;
        };
    };
}, {
    base: string[];
    badge: string[];
}, undefined, {
    variant: {
        solid: {};
        flat: {};
        faded: {
            badge: string;
        };
        shadow: {};
    };
    color: {
        default: {};
        primary: {};
        secondary: {};
        success: {};
        warning: {};
        danger: {};
    };
    size: {
        sm: {
            badge: string;
        };
        md: {
            badge: string;
        };
        lg: {
            badge: string;
        };
    };
    placement: {
        "top-right": {};
        "top-left": {};
        "bottom-right": {};
        "bottom-left": {};
    };
    shape: {
        circle: {};
        rectangle: {};
    };
    isInvisible: {
        true: {};
    };
    isOneChar: {
        true: {
            badge: string;
        };
    };
    isDot: {
        true: {};
    };
    disableAnimation: {
        true: {
            badge: string;
        };
        false: {
            badge: string;
        };
    };
    showOutline: {
        true: {
            badge: string;
        };
        false: {
            badge: string;
        };
    };
}, {
    base: string[];
    badge: string[];
}, tailwind_variants.TVReturnType<{
    variant: {
        solid: {};
        flat: {};
        faded: {
            badge: string;
        };
        shadow: {};
    };
    color: {
        default: {};
        primary: {};
        secondary: {};
        success: {};
        warning: {};
        danger: {};
    };
    size: {
        sm: {
            badge: string;
        };
        md: {
            badge: string;
        };
        lg: {
            badge: string;
        };
    };
    placement: {
        "top-right": {};
        "top-left": {};
        "bottom-right": {};
        "bottom-left": {};
    };
    shape: {
        circle: {};
        rectangle: {};
    };
    isInvisible: {
        true: {};
    };
    isOneChar: {
        true: {
            badge: string;
        };
    };
    isDot: {
        true: {};
    };
    disableAnimation: {
        true: {
            badge: string;
        };
        false: {
            badge: string;
        };
    };
    showOutline: {
        true: {
            badge: string;
        };
        false: {
            badge: string;
        };
    };
}, {
    base: string[];
    badge: string[];
}, undefined, unknown, unknown, undefined>>;
type BadgeVariantProps = VariantProps<typeof badge>;
type BadgeSlots = keyof ReturnType<typeof badge>;

export { type BadgeSlots, type BadgeVariantProps, badge };

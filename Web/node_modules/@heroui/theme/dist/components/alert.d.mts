import * as tailwind_variants from 'tailwind-variants';
import { VariantProps } from 'tailwind-variants';

/**
 * Alert wrapper **Tailwind Variants** component
 *
 * @example
 * ```js
 * const {base, mainWrapper, title, description, closeButton, alertIcon} = alert({...})
 *
 *
 * <div className={base()}>
 *    {alertIcon}
 *    <div className={mainWrapper()}>
 *       <div className={title()}>Title</div>
 *       <div className={description()}>Description</div>
 *   </div>
 *    <button>
 *      // close button
 *    </button>
 * </div>
 * ```
 */
declare const alert: tailwind_variants.TVReturnType<{
    color: {
        default: {};
        primary: {};
        secondary: {};
        success: {};
        warning: {};
        danger: {};
    };
    variant: {
        solid: {};
        flat: {};
        faded: {
            base: string;
        };
        bordered: {
            base: string;
        };
    };
    radius: {
        none: {
            base: string;
        };
        sm: {
            base: string;
        };
        md: {
            base: string;
        };
        lg: {
            base: string;
        };
        full: {
            base: string;
        };
    };
    hideIcon: {
        true: {
            iconWrapper: string;
        };
    };
    hideIconWrapper: {
        true: {
            base: string;
            iconWrapper: string;
        };
    };
    hasContent: {
        false: {
            base: string;
            mainWrapper: string;
        };
    };
}, {
    base: string;
    mainWrapper: string;
    title: string;
    description: string;
    closeButton: string;
    iconWrapper: string;
    alertIcon: string;
}, undefined, {
    color: {
        default: {};
        primary: {};
        secondary: {};
        success: {};
        warning: {};
        danger: {};
    };
    variant: {
        solid: {};
        flat: {};
        faded: {
            base: string;
        };
        bordered: {
            base: string;
        };
    };
    radius: {
        none: {
            base: string;
        };
        sm: {
            base: string;
        };
        md: {
            base: string;
        };
        lg: {
            base: string;
        };
        full: {
            base: string;
        };
    };
    hideIcon: {
        true: {
            iconWrapper: string;
        };
    };
    hideIconWrapper: {
        true: {
            base: string;
            iconWrapper: string;
        };
    };
    hasContent: {
        false: {
            base: string;
            mainWrapper: string;
        };
    };
}, {
    base: string;
    mainWrapper: string;
    title: string;
    description: string;
    closeButton: string;
    iconWrapper: string;
    alertIcon: string;
}, tailwind_variants.TVReturnType<{
    color: {
        default: {};
        primary: {};
        secondary: {};
        success: {};
        warning: {};
        danger: {};
    };
    variant: {
        solid: {};
        flat: {};
        faded: {
            base: string;
        };
        bordered: {
            base: string;
        };
    };
    radius: {
        none: {
            base: string;
        };
        sm: {
            base: string;
        };
        md: {
            base: string;
        };
        lg: {
            base: string;
        };
        full: {
            base: string;
        };
    };
    hideIcon: {
        true: {
            iconWrapper: string;
        };
    };
    hideIconWrapper: {
        true: {
            base: string;
            iconWrapper: string;
        };
    };
    hasContent: {
        false: {
            base: string;
            mainWrapper: string;
        };
    };
}, {
    base: string;
    mainWrapper: string;
    title: string;
    description: string;
    closeButton: string;
    iconWrapper: string;
    alertIcon: string;
}, undefined, unknown, unknown, undefined>>;
type AlertVariantProps = VariantProps<typeof alert>;
type AlertSlots = keyof ReturnType<typeof alert>;

export { type AlertSlots, type AlertVariantProps, alert };

import * as tailwind_variants from 'tailwind-variants';
import { VariantProps } from 'tailwind-variants';

/**
 * Checkbox wrapper **Tailwind Variants** component
 *
 * const {base, wrapper, icon, label} = checkbox({...})
 *
 * @example
 * <label className={base())}>
 *  // hidden input
 *  <span className={wrapper()} aria-hidden="true" data-selected={selected}>
 *     <svg className={icon()}>
 *       // check icon
 *     </svg>
 *  </span>
 *  <span className={label()}>Label</span>
 * </label>
 */
declare const checkbox: tailwind_variants.TVReturnType<{
    color: {
        default: {
            wrapper: string;
        };
        primary: {
            wrapper: string;
        };
        secondary: {
            wrapper: string;
        };
        success: {
            wrapper: string;
        };
        warning: {
            wrapper: string;
        };
        danger: {
            wrapper: string;
        };
    };
    size: {
        sm: {
            wrapper: string[];
            label: string;
            icon: string;
        };
        md: {
            wrapper: string[];
            label: string;
            icon: string;
        };
        lg: {
            wrapper: string[];
            label: string;
            icon: string;
        };
    };
    radius: {
        none: {
            wrapper: string;
        };
        sm: {
            wrapper: string[];
        };
        md: {
            wrapper: string[];
        };
        lg: {
            wrapper: string[];
        };
        full: {
            wrapper: string;
        };
    };
    lineThrough: {
        true: {
            label: string[];
        };
    };
    isDisabled: {
        true: {
            base: string;
        };
    };
    isInvalid: {
        true: {
            wrapper: string;
            label: string;
        };
    };
    disableAnimation: {
        true: {
            wrapper: string;
            icon: string;
            label: string;
        };
        false: {
            wrapper: string[];
            icon: string;
            label: string;
        };
    };
}, {
    base: string;
    wrapper: string[];
    hiddenInput: string[];
    icon: string;
    label: string;
}, undefined, {
    color: {
        default: {
            wrapper: string;
        };
        primary: {
            wrapper: string;
        };
        secondary: {
            wrapper: string;
        };
        success: {
            wrapper: string;
        };
        warning: {
            wrapper: string;
        };
        danger: {
            wrapper: string;
        };
    };
    size: {
        sm: {
            wrapper: string[];
            label: string;
            icon: string;
        };
        md: {
            wrapper: string[];
            label: string;
            icon: string;
        };
        lg: {
            wrapper: string[];
            label: string;
            icon: string;
        };
    };
    radius: {
        none: {
            wrapper: string;
        };
        sm: {
            wrapper: string[];
        };
        md: {
            wrapper: string[];
        };
        lg: {
            wrapper: string[];
        };
        full: {
            wrapper: string;
        };
    };
    lineThrough: {
        true: {
            label: string[];
        };
    };
    isDisabled: {
        true: {
            base: string;
        };
    };
    isInvalid: {
        true: {
            wrapper: string;
            label: string;
        };
    };
    disableAnimation: {
        true: {
            wrapper: string;
            icon: string;
            label: string;
        };
        false: {
            wrapper: string[];
            icon: string;
            label: string;
        };
    };
}, {
    base: string;
    wrapper: string[];
    hiddenInput: string[];
    icon: string;
    label: string;
}, tailwind_variants.TVReturnType<{
    color: {
        default: {
            wrapper: string;
        };
        primary: {
            wrapper: string;
        };
        secondary: {
            wrapper: string;
        };
        success: {
            wrapper: string;
        };
        warning: {
            wrapper: string;
        };
        danger: {
            wrapper: string;
        };
    };
    size: {
        sm: {
            wrapper: string[];
            label: string;
            icon: string;
        };
        md: {
            wrapper: string[];
            label: string;
            icon: string;
        };
        lg: {
            wrapper: string[];
            label: string;
            icon: string;
        };
    };
    radius: {
        none: {
            wrapper: string;
        };
        sm: {
            wrapper: string[];
        };
        md: {
            wrapper: string[];
        };
        lg: {
            wrapper: string[];
        };
        full: {
            wrapper: string;
        };
    };
    lineThrough: {
        true: {
            label: string[];
        };
    };
    isDisabled: {
        true: {
            base: string;
        };
    };
    isInvalid: {
        true: {
            wrapper: string;
            label: string;
        };
    };
    disableAnimation: {
        true: {
            wrapper: string;
            icon: string;
            label: string;
        };
        false: {
            wrapper: string[];
            icon: string;
            label: string;
        };
    };
}, {
    base: string;
    wrapper: string[];
    hiddenInput: string[];
    icon: string;
    label: string;
}, undefined, unknown, unknown, undefined>>;
/**
 * CheckboxGroup wrapper **Tailwind Variants** component
 *
 * const {base, label, wrapper} = checkboxGroup({...})
 *
 * @example
 * <div className={base())}>
 *  <label className={label()}>Label</label>
 *  <div className={wrapper()} data-orientation="vertical/horizontal">
 *     // checkboxes
 *  </div>
 * </div>
 */
declare const checkboxGroup: tailwind_variants.TVReturnType<{
    isRequired: {
        true: {
            label: string;
        };
    };
    isInvalid: {
        true: {
            description: string;
        };
    };
    disableAnimation: {
        true: {};
        false: {
            description: string;
        };
    };
}, {
    base: string;
    label: string;
    wrapper: string;
    description: string;
    errorMessage: string;
}, undefined, {
    isRequired: {
        true: {
            label: string;
        };
    };
    isInvalid: {
        true: {
            description: string;
        };
    };
    disableAnimation: {
        true: {};
        false: {
            description: string;
        };
    };
}, {
    base: string;
    label: string;
    wrapper: string;
    description: string;
    errorMessage: string;
}, tailwind_variants.TVReturnType<{
    isRequired: {
        true: {
            label: string;
        };
    };
    isInvalid: {
        true: {
            description: string;
        };
    };
    disableAnimation: {
        true: {};
        false: {
            description: string;
        };
    };
}, {
    base: string;
    label: string;
    wrapper: string;
    description: string;
    errorMessage: string;
}, undefined, unknown, unknown, undefined>>;
type CheckboxGroupSlots = keyof ReturnType<typeof checkboxGroup>;
type CheckboxVariantProps = VariantProps<typeof checkbox>;
type CheckboxSlots = keyof ReturnType<typeof checkbox>;

export { type CheckboxGroupSlots, type CheckboxSlots, type CheckboxVariantProps, checkbox, checkboxGroup };

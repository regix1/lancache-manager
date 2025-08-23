import * as tailwind_variants from 'tailwind-variants';
import { VariantProps } from 'tailwind-variants';

declare const select: tailwind_variants.TVReturnType<{
    variant: {
        flat: {
            trigger: string[];
            clearButton: string;
        };
        faded: {
            trigger: string[];
            value: string;
            clearButton: string;
        };
        bordered: {
            trigger: string[];
            value: string;
            clearButton: string;
        };
        underlined: {
            trigger: string[];
            value: string;
            clearButton: string;
        };
    };
    color: {
        default: {};
        primary: {
            selectorIcon: string;
        };
        secondary: {
            selectorIcon: string;
        };
        success: {
            selectorIcon: string;
        };
        warning: {
            selectorIcon: string;
        };
        danger: {
            selectorIcon: string;
        };
    };
    size: {
        sm: {
            label: string;
            trigger: string;
            value: string;
            clearButton: string;
        };
        md: {
            trigger: string;
            value: string;
            clearButton: string;
        };
        lg: {
            trigger: string;
            value: string;
            clearButton: string;
        };
    };
    radius: {
        none: {
            trigger: string;
        };
        sm: {
            trigger: string;
        };
        md: {
            trigger: string;
        };
        lg: {
            trigger: string;
        };
        full: {
            trigger: string;
        };
    };
    labelPlacement: {
        outside: {
            base: string;
            clearButton: string;
        };
        "outside-left": {
            base: string;
            label: string;
            clearButton: string;
        };
        inside: {
            label: string;
            trigger: string;
        };
    };
    fullWidth: {
        true: {
            base: string;
        };
        false: {
            base: string;
        };
    };
    isClearable: {
        true: {
            clearButton: string;
            endContent: string;
        };
    };
    isDisabled: {
        true: {
            base: string;
            trigger: string;
        };
    };
    isInvalid: {
        true: {
            label: string;
            value: string;
            selectorIcon: string;
        };
    };
    isRequired: {
        true: {
            label: string;
        };
    };
    isMultiline: {
        true: {
            label: string;
            trigger: string;
        };
        false: {
            value: string;
        };
    };
    disableAnimation: {
        true: {
            trigger: string;
            base: string;
            label: string;
            selectorIcon: string;
        };
        false: {
            base: string;
            label: string[];
            selectorIcon: string;
            clearButton: string[];
        };
    };
    disableSelectorIconRotation: {
        true: {};
        false: {
            selectorIcon: string;
        };
    };
}, {
    base: string[];
    label: string[];
    mainWrapper: string;
    trigger: string;
    innerWrapper: string;
    selectorIcon: string;
    spinner: string;
    value: string[];
    listboxWrapper: string;
    listbox: string;
    popoverContent: string;
    clearButton: string[];
    helperWrapper: string;
    description: string;
    errorMessage: string;
    endWrapper: string;
    endContent: string;
}, undefined, {
    variant: {
        flat: {
            trigger: string[];
            clearButton: string;
        };
        faded: {
            trigger: string[];
            value: string;
            clearButton: string;
        };
        bordered: {
            trigger: string[];
            value: string;
            clearButton: string;
        };
        underlined: {
            trigger: string[];
            value: string;
            clearButton: string;
        };
    };
    color: {
        default: {};
        primary: {
            selectorIcon: string;
        };
        secondary: {
            selectorIcon: string;
        };
        success: {
            selectorIcon: string;
        };
        warning: {
            selectorIcon: string;
        };
        danger: {
            selectorIcon: string;
        };
    };
    size: {
        sm: {
            label: string;
            trigger: string;
            value: string;
            clearButton: string;
        };
        md: {
            trigger: string;
            value: string;
            clearButton: string;
        };
        lg: {
            trigger: string;
            value: string;
            clearButton: string;
        };
    };
    radius: {
        none: {
            trigger: string;
        };
        sm: {
            trigger: string;
        };
        md: {
            trigger: string;
        };
        lg: {
            trigger: string;
        };
        full: {
            trigger: string;
        };
    };
    labelPlacement: {
        outside: {
            base: string;
            clearButton: string;
        };
        "outside-left": {
            base: string;
            label: string;
            clearButton: string;
        };
        inside: {
            label: string;
            trigger: string;
        };
    };
    fullWidth: {
        true: {
            base: string;
        };
        false: {
            base: string;
        };
    };
    isClearable: {
        true: {
            clearButton: string;
            endContent: string;
        };
    };
    isDisabled: {
        true: {
            base: string;
            trigger: string;
        };
    };
    isInvalid: {
        true: {
            label: string;
            value: string;
            selectorIcon: string;
        };
    };
    isRequired: {
        true: {
            label: string;
        };
    };
    isMultiline: {
        true: {
            label: string;
            trigger: string;
        };
        false: {
            value: string;
        };
    };
    disableAnimation: {
        true: {
            trigger: string;
            base: string;
            label: string;
            selectorIcon: string;
        };
        false: {
            base: string;
            label: string[];
            selectorIcon: string;
            clearButton: string[];
        };
    };
    disableSelectorIconRotation: {
        true: {};
        false: {
            selectorIcon: string;
        };
    };
}, {
    base: string[];
    label: string[];
    mainWrapper: string;
    trigger: string;
    innerWrapper: string;
    selectorIcon: string;
    spinner: string;
    value: string[];
    listboxWrapper: string;
    listbox: string;
    popoverContent: string;
    clearButton: string[];
    helperWrapper: string;
    description: string;
    errorMessage: string;
    endWrapper: string;
    endContent: string;
}, tailwind_variants.TVReturnType<{
    variant: {
        flat: {
            trigger: string[];
            clearButton: string;
        };
        faded: {
            trigger: string[];
            value: string;
            clearButton: string;
        };
        bordered: {
            trigger: string[];
            value: string;
            clearButton: string;
        };
        underlined: {
            trigger: string[];
            value: string;
            clearButton: string;
        };
    };
    color: {
        default: {};
        primary: {
            selectorIcon: string;
        };
        secondary: {
            selectorIcon: string;
        };
        success: {
            selectorIcon: string;
        };
        warning: {
            selectorIcon: string;
        };
        danger: {
            selectorIcon: string;
        };
    };
    size: {
        sm: {
            label: string;
            trigger: string;
            value: string;
            clearButton: string;
        };
        md: {
            trigger: string;
            value: string;
            clearButton: string;
        };
        lg: {
            trigger: string;
            value: string;
            clearButton: string;
        };
    };
    radius: {
        none: {
            trigger: string;
        };
        sm: {
            trigger: string;
        };
        md: {
            trigger: string;
        };
        lg: {
            trigger: string;
        };
        full: {
            trigger: string;
        };
    };
    labelPlacement: {
        outside: {
            base: string;
            clearButton: string;
        };
        "outside-left": {
            base: string;
            label: string;
            clearButton: string;
        };
        inside: {
            label: string;
            trigger: string;
        };
    };
    fullWidth: {
        true: {
            base: string;
        };
        false: {
            base: string;
        };
    };
    isClearable: {
        true: {
            clearButton: string;
            endContent: string;
        };
    };
    isDisabled: {
        true: {
            base: string;
            trigger: string;
        };
    };
    isInvalid: {
        true: {
            label: string;
            value: string;
            selectorIcon: string;
        };
    };
    isRequired: {
        true: {
            label: string;
        };
    };
    isMultiline: {
        true: {
            label: string;
            trigger: string;
        };
        false: {
            value: string;
        };
    };
    disableAnimation: {
        true: {
            trigger: string;
            base: string;
            label: string;
            selectorIcon: string;
        };
        false: {
            base: string;
            label: string[];
            selectorIcon: string;
            clearButton: string[];
        };
    };
    disableSelectorIconRotation: {
        true: {};
        false: {
            selectorIcon: string;
        };
    };
}, {
    base: string[];
    label: string[];
    mainWrapper: string;
    trigger: string;
    innerWrapper: string;
    selectorIcon: string;
    spinner: string;
    value: string[];
    listboxWrapper: string;
    listbox: string;
    popoverContent: string;
    clearButton: string[];
    helperWrapper: string;
    description: string;
    errorMessage: string;
    endWrapper: string;
    endContent: string;
}, undefined, unknown, unknown, undefined>>;
type SelectVariantProps = VariantProps<typeof select>;
type SelectSlots = keyof ReturnType<typeof select>;

export { type SelectSlots, type SelectVariantProps, select };

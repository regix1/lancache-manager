import * as tailwind_variants from 'tailwind-variants';
import { VariantProps } from 'tailwind-variants';

declare const autocomplete: tailwind_variants.TVReturnType<{
    isClearable: {
        true: {};
        false: {
            clearButton: string;
        };
    };
    disableAnimation: {
        true: {
            selectorButton: string;
        };
        false: {
            selectorButton: string;
        };
    };
    disableSelectorIconRotation: {
        true: {};
        false: {
            selectorButton: string;
        };
    };
}, {
    base: string;
    listboxWrapper: string;
    listbox: string;
    popoverContent: string;
    endContentWrapper: string;
    clearButton: string[];
    selectorButton: string;
}, undefined, {
    isClearable: {
        true: {};
        false: {
            clearButton: string;
        };
    };
    disableAnimation: {
        true: {
            selectorButton: string;
        };
        false: {
            selectorButton: string;
        };
    };
    disableSelectorIconRotation: {
        true: {};
        false: {
            selectorButton: string;
        };
    };
}, {
    base: string;
    listboxWrapper: string;
    listbox: string;
    popoverContent: string;
    endContentWrapper: string;
    clearButton: string[];
    selectorButton: string;
}, tailwind_variants.TVReturnType<{
    isClearable: {
        true: {};
        false: {
            clearButton: string;
        };
    };
    disableAnimation: {
        true: {
            selectorButton: string;
        };
        false: {
            selectorButton: string;
        };
    };
    disableSelectorIconRotation: {
        true: {};
        false: {
            selectorButton: string;
        };
    };
}, {
    base: string;
    listboxWrapper: string;
    listbox: string;
    popoverContent: string;
    endContentWrapper: string;
    clearButton: string[];
    selectorButton: string;
}, undefined, unknown, unknown, undefined>>;
type AutocompleteVariantProps = VariantProps<typeof autocomplete>;
type AutocompleteSlots = keyof ReturnType<typeof autocomplete>;

export { type AutocompleteSlots, type AutocompleteVariantProps, autocomplete };

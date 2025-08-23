import * as tailwind_variants from 'tailwind-variants';
import { VariantProps } from 'tailwind-variants';

/**
 * Snippet wrapper **Tailwind Variants** component
 *
 * const {base, content, pre, copy} = snippet({...})
 *
 * @example
 * <div className={base())}>
 *   <pre className={pre()}>
 *      // code snippet
 *      <div className={content()}>...</div>
 *   </pre>
 *   <button className={copy()}>
 *    <svg>
 *    // copy icon / check icon
 *    </svg>
 *  </button>
 * </div>
 */
declare const snippet: tailwind_variants.TVReturnType<{
    variant: {
        flat: string;
        solid: string;
        bordered: string;
        shadow: string;
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
            base: string;
        };
        md: {
            base: string;
        };
        lg: {
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
    };
    fullWidth: {
        true: {
            base: string;
        };
    };
    disableAnimation: {
        true: {};
        false: {
            copyIcon: string;
            checkIcon: string;
        };
    };
}, {
    base: string;
    pre: string;
    content: string;
    symbol: string;
    copyButton: string[];
    copyIcon: string[];
    checkIcon: string[];
}, undefined, {
    variant: {
        flat: string;
        solid: string;
        bordered: string;
        shadow: string;
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
            base: string;
        };
        md: {
            base: string;
        };
        lg: {
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
    };
    fullWidth: {
        true: {
            base: string;
        };
    };
    disableAnimation: {
        true: {};
        false: {
            copyIcon: string;
            checkIcon: string;
        };
    };
}, {
    base: string;
    pre: string;
    content: string;
    symbol: string;
    copyButton: string[];
    copyIcon: string[];
    checkIcon: string[];
}, tailwind_variants.TVReturnType<{
    variant: {
        flat: string;
        solid: string;
        bordered: string;
        shadow: string;
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
            base: string;
        };
        md: {
            base: string;
        };
        lg: {
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
    };
    fullWidth: {
        true: {
            base: string;
        };
    };
    disableAnimation: {
        true: {};
        false: {
            copyIcon: string;
            checkIcon: string;
        };
    };
}, {
    base: string;
    pre: string;
    content: string;
    symbol: string;
    copyButton: string[];
    copyIcon: string[];
    checkIcon: string[];
}, undefined, unknown, unknown, undefined>>;
type SnippetVariantProps = VariantProps<typeof snippet>;
type SnippetSlots = keyof ReturnType<typeof snippet>;

export { type SnippetSlots, type SnippetVariantProps, snippet };

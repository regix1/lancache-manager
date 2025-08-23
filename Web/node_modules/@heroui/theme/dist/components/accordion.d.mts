import * as tailwind_variants from 'tailwind-variants';
import { VariantProps } from 'tailwind-variants';

/**
 * Accordion wrapper **Tailwind Variants** component
 *
 * const styles = accordion({...})
 *
 * @example
 * <div role="group" className={styles())}>
 *   // accordion elements
 * </div>
 */
declare const accordion: tailwind_variants.TVReturnType<{
    variant: {
        light: string;
        shadow: string;
        bordered: string;
        splitted: string;
    };
    fullWidth: {
        true: string;
    };
}, undefined, "px-2", {
    variant: {
        light: string;
        shadow: string;
        bordered: string;
        splitted: string;
    };
    fullWidth: {
        true: string;
    };
}, undefined, tailwind_variants.TVReturnType<{
    variant: {
        light: string;
        shadow: string;
        bordered: string;
        splitted: string;
    };
    fullWidth: {
        true: string;
    };
}, undefined, "px-2", unknown, unknown, undefined>>;
/**
 * AccordionItem wrapper **Tailwind Variants** component
 *
 * const {base, heading, indicator, trigger, startContent, title, subtitle, content } = accordionItem({...})
 *
 * @example
 * <div className={base())}>
 *   <div className={heading())}>
 *    <button className={trigger())}>
 *      <div className={startContent()}>
 *         // content
 *      </div>
 *      <div className={titleWrapper()}>
 *        <h3 className={title())}>Title</h3>
 *        <span className={subtitle())}>Subtitle</span>
 *      </div>
 *      <span className={indicator())}>Indicator</span>
 *    </button>
 *  </div>
 *  <div className={content())}>Content</div>
 * </div>
 */
declare const accordionItem: tailwind_variants.TVReturnType<{
    variant: {
        splitted: {
            base: string;
        };
    };
    isCompact: {
        true: {
            trigger: string;
            title: string;
            subtitle: string;
            indicator: string;
            content: string;
        };
    };
    isDisabled: {
        true: {
            base: string;
        };
    };
    hideIndicator: {
        true: {
            indicator: string;
        };
    };
    disableAnimation: {
        true: {
            content: string;
        };
        false: {
            indicator: string;
            trigger: string;
        };
    };
    disableIndicatorAnimation: {
        true: {
            indicator: string;
        };
        false: {
            indicator: string;
        };
    };
}, {
    base: string;
    heading: string;
    trigger: string[];
    startContent: string;
    indicator: string;
    titleWrapper: string;
    title: string;
    subtitle: string;
    content: string;
}, undefined, {
    variant: {
        splitted: {
            base: string;
        };
    };
    isCompact: {
        true: {
            trigger: string;
            title: string;
            subtitle: string;
            indicator: string;
            content: string;
        };
    };
    isDisabled: {
        true: {
            base: string;
        };
    };
    hideIndicator: {
        true: {
            indicator: string;
        };
    };
    disableAnimation: {
        true: {
            content: string;
        };
        false: {
            indicator: string;
            trigger: string;
        };
    };
    disableIndicatorAnimation: {
        true: {
            indicator: string;
        };
        false: {
            indicator: string;
        };
    };
}, {
    base: string;
    heading: string;
    trigger: string[];
    startContent: string;
    indicator: string;
    titleWrapper: string;
    title: string;
    subtitle: string;
    content: string;
}, tailwind_variants.TVReturnType<{
    variant: {
        splitted: {
            base: string;
        };
    };
    isCompact: {
        true: {
            trigger: string;
            title: string;
            subtitle: string;
            indicator: string;
            content: string;
        };
    };
    isDisabled: {
        true: {
            base: string;
        };
    };
    hideIndicator: {
        true: {
            indicator: string;
        };
    };
    disableAnimation: {
        true: {
            content: string;
        };
        false: {
            indicator: string;
            trigger: string;
        };
    };
    disableIndicatorAnimation: {
        true: {
            indicator: string;
        };
        false: {
            indicator: string;
        };
    };
}, {
    base: string;
    heading: string;
    trigger: string[];
    startContent: string;
    indicator: string;
    titleWrapper: string;
    title: string;
    subtitle: string;
    content: string;
}, undefined, unknown, unknown, undefined>>;
type AccordionGroupVariantProps = VariantProps<typeof accordion>;
type AccordionItemVariantProps = VariantProps<typeof accordionItem>;
type AccordionItemSlots = keyof ReturnType<typeof accordionItem>;

export { type AccordionGroupVariantProps, type AccordionItemSlots, type AccordionItemVariantProps, accordion, accordionItem };

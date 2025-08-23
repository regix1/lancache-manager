import * as tailwind_variants from 'tailwind-variants';
import { VariantProps } from 'tailwind-variants';
import * as tailwind_merge from 'tailwind-merge';

/**
 * Menu wrapper **Tailwind Variants** component
 *
 * const classNames = menu({...})

 */
declare const menu: tailwind_variants.TVReturnType<{
    [key: string]: {
        [key: string]: tailwind_merge.ClassNameValue | {
            base?: tailwind_merge.ClassNameValue;
            list?: tailwind_merge.ClassNameValue;
            emptyContent?: tailwind_merge.ClassNameValue;
        };
    };
} | {
    [x: string]: {
        [x: string]: tailwind_merge.ClassNameValue | {
            base?: tailwind_merge.ClassNameValue;
            list?: tailwind_merge.ClassNameValue;
            emptyContent?: tailwind_merge.ClassNameValue;
        };
    };
} | {}, {
    base: string;
    list: string;
    emptyContent: string[];
}, undefined, {
    [key: string]: {
        [key: string]: tailwind_merge.ClassNameValue | {
            base?: tailwind_merge.ClassNameValue;
            list?: tailwind_merge.ClassNameValue;
            emptyContent?: tailwind_merge.ClassNameValue;
        };
    };
} | {}, {
    base: string;
    list: string;
    emptyContent: string[];
}, tailwind_variants.TVReturnType<unknown, {
    base: string;
    list: string;
    emptyContent: string[];
}, undefined, unknown, unknown, undefined>>;
/**
 * MenuItem wrapper **Tailwind Variants** component
 *
 * const {base, heading, indicator, trigger, leftIndicator, title, subtitle, content } = menuItem({...})
 *
 * @example
 * <div className={base())} data-focus-visible={boolean} data-hover={boolean}>
 *   <div className={heading())}>
 *    <button className={trigger())}>
 *      <div className={leftIndicator()}>
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
declare const menuItem: tailwind_variants.TVReturnType<{
    variant: {
        solid: {
            base: string;
        };
        bordered: {
            base: string;
        };
        light: {
            base: string;
        };
        faded: {
            base: string[];
        };
        flat: {
            base: string;
        };
        shadow: {
            base: string;
        };
    };
    color: {
        default: {};
        primary: {};
        secondary: {};
        success: {};
        warning: {};
        danger: {};
    };
    showDivider: {
        true: {
            base: string[];
        };
        false: {};
    };
    isDisabled: {
        true: {
            base: string;
        };
    };
    disableAnimation: {
        true: {};
        false: {
            base: string;
        };
    };
    hasTitleTextChild: {
        true: {
            title: string;
        };
    };
    hasDescriptionTextChild: {
        true: {
            description: string;
        };
    };
}, {
    base: string[];
    wrapper: string;
    title: string;
    description: string[];
    selectedIcon: string[];
    shortcut: string[];
}, undefined, {
    variant: {
        solid: {
            base: string;
        };
        bordered: {
            base: string;
        };
        light: {
            base: string;
        };
        faded: {
            base: string[];
        };
        flat: {
            base: string;
        };
        shadow: {
            base: string;
        };
    };
    color: {
        default: {};
        primary: {};
        secondary: {};
        success: {};
        warning: {};
        danger: {};
    };
    showDivider: {
        true: {
            base: string[];
        };
        false: {};
    };
    isDisabled: {
        true: {
            base: string;
        };
    };
    disableAnimation: {
        true: {};
        false: {
            base: string;
        };
    };
    hasTitleTextChild: {
        true: {
            title: string;
        };
    };
    hasDescriptionTextChild: {
        true: {
            description: string;
        };
    };
}, {
    base: string[];
    wrapper: string;
    title: string;
    description: string[];
    selectedIcon: string[];
    shortcut: string[];
}, tailwind_variants.TVReturnType<{
    variant: {
        solid: {
            base: string;
        };
        bordered: {
            base: string;
        };
        light: {
            base: string;
        };
        faded: {
            base: string[];
        };
        flat: {
            base: string;
        };
        shadow: {
            base: string;
        };
    };
    color: {
        default: {};
        primary: {};
        secondary: {};
        success: {};
        warning: {};
        danger: {};
    };
    showDivider: {
        true: {
            base: string[];
        };
        false: {};
    };
    isDisabled: {
        true: {
            base: string;
        };
    };
    disableAnimation: {
        true: {};
        false: {
            base: string;
        };
    };
    hasTitleTextChild: {
        true: {
            title: string;
        };
    };
    hasDescriptionTextChild: {
        true: {
            description: string;
        };
    };
}, {
    base: string[];
    wrapper: string;
    title: string;
    description: string[];
    selectedIcon: string[];
    shortcut: string[];
}, undefined, unknown, unknown, undefined>>;
/**
 * Menu section wrapper **Tailwind Variants** component
 *
 * const { base, section, heading } = menuSection({...})
 *
 * @example
 * <div className={base()}>
 *  <button className={trigger()} aria-expanded="true/false">your trigger</button>
 *  <div className={section()}>
 *    // menu content
 *    <span className={arrow()} data-placement="top/bottom/left/right..." /> // arrow
 *  </div>
 * </div>
 */
declare const menuSection: tailwind_variants.TVReturnType<{
    [key: string]: {
        [key: string]: tailwind_merge.ClassNameValue | {
            divider?: tailwind_merge.ClassNameValue;
            base?: tailwind_merge.ClassNameValue;
            group?: tailwind_merge.ClassNameValue;
            heading?: tailwind_merge.ClassNameValue;
        };
    };
} | {
    [x: string]: {
        [x: string]: tailwind_merge.ClassNameValue | {
            divider?: tailwind_merge.ClassNameValue;
            base?: tailwind_merge.ClassNameValue;
            group?: tailwind_merge.ClassNameValue;
            heading?: tailwind_merge.ClassNameValue;
        };
    };
} | {}, {
    base: string;
    heading: string;
    group: string;
    divider: string;
}, undefined, {
    [key: string]: {
        [key: string]: tailwind_merge.ClassNameValue | {
            divider?: tailwind_merge.ClassNameValue;
            base?: tailwind_merge.ClassNameValue;
            group?: tailwind_merge.ClassNameValue;
            heading?: tailwind_merge.ClassNameValue;
        };
    };
} | {}, {
    base: string;
    heading: string;
    group: string;
    divider: string;
}, tailwind_variants.TVReturnType<unknown, {
    base: string;
    heading: string;
    group: string;
    divider: string;
}, undefined, unknown, unknown, undefined>>;
type MenuVariantProps = VariantProps<typeof menu>;
type MenuSlots = keyof ReturnType<typeof menu>;
type MenuSectionVariantProps = VariantProps<typeof menuSection>;
type MenuSectionSlots = keyof ReturnType<typeof menuSection>;
type MenuItemVariantProps = VariantProps<typeof menuItem>;
type MenuItemSlots = keyof ReturnType<typeof menuItem>;

export { type MenuItemSlots, type MenuItemVariantProps, type MenuSectionSlots, type MenuSectionVariantProps, type MenuSlots, type MenuVariantProps, menu, menuItem, menuSection };

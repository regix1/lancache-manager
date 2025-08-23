import * as tailwind_variants from 'tailwind-variants';
import { VariantProps } from 'tailwind-variants';

/**
 * ScrollShadow wrapper **Tailwind Variants** component
 *
 * const classNames = scrollShadow({...})
 *
 * @example
 * <div className={classNames)}>
 *   Lorem ipsum dolor sit amet, consectetur adipiscing elit.
 *   ...
 * </div>
 */
declare const scrollShadow: tailwind_variants.TVReturnType<{
    orientation: {
        vertical: string[];
        horizontal: string[];
    };
    hideScrollBar: {
        true: string;
        false: string;
    };
}, undefined, never[], {
    orientation: {
        vertical: string[];
        horizontal: string[];
    };
    hideScrollBar: {
        true: string;
        false: string;
    };
}, undefined, tailwind_variants.TVReturnType<{
    orientation: {
        vertical: string[];
        horizontal: string[];
    };
    hideScrollBar: {
        true: string;
        false: string;
    };
}, undefined, never[], unknown, unknown, undefined>>;
type ScrollShadowVariantProps = VariantProps<typeof scrollShadow>;

export { type ScrollShadowVariantProps, scrollShadow };

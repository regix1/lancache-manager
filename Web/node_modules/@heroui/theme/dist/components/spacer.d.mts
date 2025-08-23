import * as tailwind_variants from 'tailwind-variants';
import { VariantProps } from 'tailwind-variants';

/**
 * Spacer wrapper **Tailwind Variants** component
 *
 * @example
 *
 * const styles = spacer()
 *
 * <span className={styles} />
 */
declare const spacer: tailwind_variants.TVReturnType<{
    isInline: {
        true: string;
        false: string;
    };
}, undefined, "w-px h-px inline-block", {
    isInline: {
        true: string;
        false: string;
    };
}, undefined, tailwind_variants.TVReturnType<{
    isInline: {
        true: string;
        false: string;
    };
}, undefined, "w-px h-px inline-block", unknown, unknown, undefined>>;
type SpacerVariantProps = VariantProps<typeof spacer>;

export { type SpacerVariantProps, spacer };

import * as tailwind_variants from 'tailwind-variants';
import { VariantProps } from 'tailwind-variants';

/**
 * Divider wrapper **Tailwind Variants** component
 *
 * @example
 *
 * const styles = divider()
 *
 * <span className={styles} />
 */
declare const divider: tailwind_variants.TVReturnType<{
    orientation: {
        horizontal: string;
        vertical: string;
    };
}, undefined, "shrink-0 bg-divider border-none", {
    orientation: {
        horizontal: string;
        vertical: string;
    };
}, undefined, tailwind_variants.TVReturnType<{
    orientation: {
        horizontal: string;
        vertical: string;
    };
}, undefined, "shrink-0 bg-divider border-none", unknown, unknown, undefined>>;
type DividerVariantProps = VariantProps<typeof divider>;

export { type DividerVariantProps, divider };

import * as tailwind_variants from 'tailwind-variants';
import { VariantProps } from 'tailwind-variants';

/**
 * Skeleton wrapper **Tailwind Variants** component
 *
 * const styles = skeleton({...})
 *
 * @example
 * <div className={styles()}>
 *   // skeleton content
 * </div>
 */
declare const skeleton: tailwind_variants.TVReturnType<{
    disableAnimation: {
        true: {
            base: string;
            content: string;
        };
        false: {
            base: string;
            content: string;
        };
    };
}, {
    base: string[];
    content: string[];
}, undefined, {
    disableAnimation: {
        true: {
            base: string;
            content: string;
        };
        false: {
            base: string;
            content: string;
        };
    };
}, {
    base: string[];
    content: string[];
}, tailwind_variants.TVReturnType<{
    disableAnimation: {
        true: {
            base: string;
            content: string;
        };
        false: {
            base: string;
            content: string;
        };
    };
}, {
    base: string[];
    content: string[];
}, undefined, unknown, unknown, undefined>>;
type SkeletonVariantProps = VariantProps<typeof skeleton>;
type SkeletonSlots = keyof ReturnType<typeof skeleton>;

export { type SkeletonSlots, type SkeletonVariantProps, skeleton };

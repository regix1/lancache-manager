import * as tailwind_variants from 'tailwind-variants';
import { VariantProps } from 'tailwind-variants';

/**
 * Card **Tailwind Variants** component
 *
 * @example
 * ```js
 * const classNames = form({...})
 *
 * <form className={base()}>
 *    // form content
 * </form>
 * ```
 */
declare const form: tailwind_variants.TVReturnType<{} | {} | {}, undefined, "flex flex-col gap-2 items-start", {} | {}, undefined, tailwind_variants.TVReturnType<unknown, undefined, "flex flex-col gap-2 items-start", unknown, unknown, undefined>>;
type FormVariantProps = VariantProps<typeof form>;

export { type FormVariantProps, form };

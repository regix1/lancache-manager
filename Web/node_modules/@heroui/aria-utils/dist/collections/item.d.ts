export { Item as BaseItem } from '@react-stately/collections';
import { ItemProps as ItemProps$1 } from '@react-types/shared';
import { As, HTMLHeroUIProps } from '@heroui/system';

/**
 * A modified version of the ItemProps from @react-types/shared, with the addition of the HeroUI props.
 *
 */
type ItemProps<Type extends As = "div", T extends object = {}> = ItemProps$1<T> & HTMLHeroUIProps<Type>;

export type { ItemProps };

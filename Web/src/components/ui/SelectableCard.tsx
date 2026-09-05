import { useId, type ReactNode } from 'react';
import { Tooltip } from './Tooltip';

interface SelectableCardProps {
  /** Radio group shared by every card competing for the same choice. */
  name: string;
  value: string;
  checked: boolean;
  onChange: () => void;
  /**
   * Called when the checked card is clicked again. A native radio cannot clear itself, so a
   * group that may end with nothing chosen names what happens here; groups that always keep
   * one selection leave it unset.
   */
  onDeselect?: () => void;
  disabled?: boolean;
  /** Explains a disabled choice on hover, keyboard focus, or touch. */
  disabledReason?: string;
  /** Mark or icon, already coloured by the caller; the card sets its size. */
  icon?: ReactNode;
  title: string;
  description?: ReactNode;
  note?: ReactNode;
  /** Trailing status beside the title, e.g. a Badge. */
  badge?: ReactNode;
  /**
   * `row` puts the radio beside the text, for choices explained in a sentence or two. `stack`
   * centres icon, title and note under a corner radio, for a grid of short equal-weight choices
   * whose cards must line up with each other.
   */
  layout?: 'row' | 'stack';
}

/**
 * One bordered option in a choice group. The whole card is the label of a native radio, so the
 * card is clicked, tabbed to and arrowed between like any radio group, the ring is drawn around
 * the card while the radio has keyboard focus, and the title, description and note are read out
 * with it. Every line is rendered whether or not the card is selected, so choosing one never
 * moves its neighbours.
 */
export const SelectableCard: React.FC<SelectableCardProps> = ({
  name,
  value,
  checked,
  onChange,
  onDeselect,
  disabled,
  disabledReason,
  icon,
  title,
  description,
  note,
  badge,
  layout = 'row'
}) => {
  const id = useId();
  const descriptionId = `${id}-description`;
  const noteId = `${id}-note`;
  const reasonId = `${id}-reason`;
  const blocked = disabled && !!disabledReason;
  const describedBy = [
    description ? descriptionId : null,
    note ? noteId : null,
    blocked ? reasonId : null
  ]
    .filter(Boolean)
    .join(' ');
  // The modifier is written out in full: the stylesheet only keeps a layered rule whose class
  // appears somewhere in the source as written, and a name pieced together at runtime never
  // does, so the stacked card lost its centring and top inset while its child rules survived.
  const layoutClassName = layout === 'stack' ? ' selectable-card--stack' : '';
  const checkedClassName = checked ? ' selectable-card--checked' : '';

  const card = (
    <label className={`selectable-card${layoutClassName}${checkedClassName}`}>
      <input
        type="radio"
        name={name}
        value={value}
        checked={checked}
        disabled={disabled}
        className="selectable-card__radio"
        onChange={onChange}
        onClick={onDeselect && checked ? onDeselect : undefined}
        onKeyDown={(event) => {
          // Space on an already-checked radio raises no click, so the keyboard clears the card
          // the same way a second click does.
          if (onDeselect && checked && event.key === ' ') {
            event.preventDefault();
            onDeselect();
          }
        }}
        aria-describedby={describedBy || undefined}
      />
      <span className="selectable-card__body">
        <span className="selectable-card__heading">
          {icon && (
            <span className="selectable-card__icon" aria-hidden="true">
              {icon}
            </span>
          )}
          <span className="selectable-card__title">{title}</span>
          {badge && <span className="selectable-card__badge">{badge}</span>}
        </span>
        {description && (
          <span id={descriptionId} className="selectable-card__description">
            {description}
          </span>
        )}
        {note && (
          <span id={noteId} className="selectable-card__note">
            {note}
          </span>
        )}
      </span>
    </label>
  );

  if (!blocked) return card;

  return (
    <Tooltip content={disabledReason} className="h-full min-w-0">
      <div
        className="selectable-card__help"
        role="group"
        tabIndex={0}
        aria-label={title}
        aria-describedby={reasonId}
      >
        {card}
        <span id={reasonId} className="sr-only">
          {disabledReason}
        </span>
      </div>
    </Tooltip>
  );
};

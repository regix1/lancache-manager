import { noAutofill } from '@utils/autofill';
import React, { useState, useRef, useEffect, useCallback } from 'react';
import { createPortal } from 'react-dom';
import { HexColorPicker, HexColorInput } from 'react-colorful';
import { Percent, Copy, Check, RotateCcw, X } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { Tooltip } from '@components/ui/Tooltip';
import { Slider } from '@components/ui/Slider';
import Badge from '@components/ui/Badge';
import { Button } from '@components/ui/Button';
import { useAnchoredPanel, type PanelPlacement, type PanelSpace } from '@hooks/useAnchoredPanel';
import { clampToViewport, MENU_GUTTER_PX } from '@utils/viewportClamp';
import { hexToRgba, readColorChannels } from '@services/themeSchema';
import '@/styles/features/theme-editor-form.css';

/** Gap between the swatch and the picker on whichever side it opens. */
const SWATCH_GAP_PX = 8;
/** Pre-measurement guess, used only on the pass before the picker is in the DOM. */
const PICKER_WIDTH_PX = 250;
/** Extra room the right side must offer before the picker prefers it. */
const RIGHT_SIDE_MARGIN_PX = 20;
/**
 * The opacity out of an rgba() value. The three channels come from `readColorChannels`,
 * which reads past the alpha and drops it because most of its callers pick their own.
 */
const RGBA_ALPHA = /^rgba\(\s*\d+[\s,]+\d+[\s,]+\d+\s*[,/]\s*([\d.]+)\s*\)$/i;

interface ImprovedColorPickerProps {
  value: string;
  onChange: (value: string) => void;
  onStart?: () => void;
  onColorCommit?: (previousColor: string) => void; // Called when picker closes with the original color
  supportsAlpha?: boolean;
  label?: string;
  description?: string;
  affects?: string[];
  copiedColor?: string | null;
  onCopy?: (color: string) => void;
  onRestore?: () => void;
  hasHistory?: boolean;
}

export const ImprovedColorPicker: React.FC<ImprovedColorPickerProps> = ({
  value,
  onChange,
  onStart,
  onColorCommit,
  supportsAlpha = false,
  label,
  description,
  affects = [],
  copiedColor,
  onCopy,
  onRestore,
  hasHistory = false
}) => {
  const { t } = useTranslation();
  const [showPicker, setShowPicker] = useState(false);
  const [hexValue, setHexValue] = useState('');
  const [alpha, setAlpha] = useState(1);
  const [hexInvalid, setHexInvalid] = useState(false);
  const buttonRef = useRef<HTMLButtonElement>(null);
  const popupRef = useRef<HTMLDivElement>(null);
  const colorBeforeEdit = useRef<string | null>(null);

  // Parse color value (hex or rgba) - skip while picker is open (local state is authoritative during drag)
  useEffect(() => {
    if (showPicker) return;
    const channels = readColorChannels(value);
    if (!channels) return;
    setHexValue(
      '#' + channels.map((channel: number) => channel.toString(16).padStart(2, '0')).join('')
    );
    const opacity = RGBA_ALPHA.exec(value.trim());
    setAlpha(opacity ? parseFloat(opacity[1]) : 1);
  }, [value, showPicker]);

  /** Hands the color the edit started from to the history, so the edit can be undone. */
  const commitEdit = useCallback((): void => {
    if (onColorCommit && colorBeforeEdit.current && colorBeforeEdit.current !== value) {
      onColorCommit(colorBeforeEdit.current);
    }
    colorBeforeEdit.current = null;
  }, [onColorCommit, value]);

  /** Closes the picker, recording the color it opened on so the edit can be undone. */
  const closePicker = useCallback((): void => {
    commitEdit();
    setShowPicker(false);
  }, [commitEdit]);

  /**
   * The picker sits BESIDE its swatch rather than under it, so the shared below/above
   * placement does not apply. It prefers the right, falls back to the left, and clamps
   * on both axes: a swatch low in a long theme form used to hang the picker off the
   * bottom of the screen, because only the horizontal edge was ever pulled back.
   */
  const place = useCallback((space: PanelSpace): PanelPlacement => {
    const { anchor, panelWidth, panelHeight, viewportWidth, viewportHeight, gutter } = space;
    const pickerWidth = panelWidth || PICKER_WIDTH_PX;
    const spaceOnRight = viewportWidth - anchor.right;

    // Prefer the right of the swatch, fall back to its left. Neither side is
    // guaranteed to fit on a narrow screen, where a swatch close to the left edge
    // leaves less than the picker's width beside it, so the chosen edge is then
    // pulled back onto the screen.
    const desiredLeft =
      spaceOnRight > pickerWidth + RIGHT_SIDE_MARGIN_PX
        ? anchor.right + SWATCH_GAP_PX
        : anchor.left - pickerWidth - SWATCH_GAP_PX;

    return {
      left: clampToViewport(desiredLeft, pickerWidth, viewportWidth, gutter),
      top: clampToViewport(anchor.top, panelHeight, viewportHeight, gutter),
      openUpward: false
    };
  }, []);

  const { present, closing, position } = useAnchoredPanel({
    open: showPicker,
    anchorRef: buttonRef,
    panelRef: popupRef,
    onClose: closePicker,
    gutter: MENU_GUTTER_PX,
    place
  });

  // Click-outside only. Escape is handled below, and the close-on-scroll listener is gone:
  // the picker is positioned on the page and carried by its swatch, so scrolling keeps
  // them together.
  useEffect(() => {
    if (!showPicker) return;

    const handleClickOutside = (event: MouseEvent) => {
      const target = event.target as Node;
      // Check if click is outside both the button and the popup
      const isOutsideButton = buttonRef.current && !buttonRef.current.contains(target);
      const isOutsidePopup = popupRef.current && !popupRef.current.contains(target);

      if (isOutsideButton && isOutsidePopup) {
        closePicker();
      }
    };

    document.addEventListener('mousedown', handleClickOutside);
    return () => document.removeEventListener('mousedown', handleClickOutside);
  }, [showPicker, closePicker]);

  // One Escape closes the picker and only the picker. The dialog around it handles Escape
  // through React, which delegates from the portal container the dialog is mounted in, so
  // a keypress that is left to bubble reaches the dialog first and takes the whole form
  // down with the popover. Listening in the capture phase on the document puts this ahead
  // of both that delegation and the shared panel hook's own Escape.
  useEffect(() => {
    if (!showPicker) return;

    const handleEscape = (event: KeyboardEvent): void => {
      if (event.key !== 'Escape') return;
      event.stopPropagation();
      closePicker();
    };

    document.addEventListener('keydown', handleEscape, true);
    return () => document.removeEventListener('keydown', handleEscape, true);
  }, [showPicker, closePicker]);

  const handleHexChange = (newHex: string) => {
    setHexValue(newHex);
    const colorValue = supportsAlpha && alpha < 1 ? hexToRgba(newHex, alpha) : newHex;
    onChange(colorValue);
  };

  const handleAlphaChange = (newAlpha: number) => {
    setAlpha(newAlpha);
    const colorValue = newAlpha < 1 ? hexToRgba(hexValue, newAlpha) : hexValue;
    onChange(colorValue);
  };

  const handlePickerToggle = () => {
    if (!showPicker) {
      if (onStart) onStart();
      colorBeforeEdit.current = value;
      setShowPicker(true);
    } else {
      closePicker();
    }
  };

  /**
   * Flags a value the color parser cannot read. On blur rather than per keystroke, because
   * a half-typed `#ff` is not a mistake yet and marking it while the caret is still in the
   * field says the reader got it wrong before they have finished.
   */
  const handleHexBlur = (): void => {
    setHexInvalid(value.trim() !== '' && readColorChannels(value) === null);
    commitEdit();
  };

  return (
    <div className="grid grid-cols-1 sm:grid-cols-[1fr_auto] gap-4 items-start">
      {/* Label and description */}
      <div>
        {label && <label className="form-field-label">{label}</label>}
        {description && <p className="text-xs text-themed-muted">{description}</p>}
        {affects.length > 0 && (
          <div className="flex flex-wrap gap-1 mt-1">
            {affects.map((item, idx) => (
              <Badge key={idx} variant="neutral">
                {item}
              </Badge>
            ))}
          </div>
        )}
      </div>

      {/* Color controls */}
      <div className="theme-color-row__controls flex items-center gap-2">
        {/* Color preview button */}
        <div className="relative flex">
          <Tooltip content={t('modals.theme.colorPicker.pickColor')} position="top">
            <button
              ref={buttonRef}
              type="button"
              onClick={handlePickerToggle}
              aria-label={t('modals.theme.colorPicker.pickColor')}
              className="theme-color-swatch themed-border-radius-sm border-2 cursor-pointer border-themed-secondary"
              style={{ '--color-swatch-fill': value } as React.CSSProperties}
            />
          </Tooltip>

          {/* Color picker popover - rendered via portal.
              Rendered while `present` (not just `showPicker`) so the exit animation plays. */}
          {present &&
            createPortal(
              <div
                ref={popupRef}
                className={`absolute z-[100001] p-3 rounded-lg shadow-2xl overflow-hidden bg-themed-primary border border-themed-primary isolate motion-reduce:animate-none ${
                  closing
                    ? 'animate-[dropdownSlideOutDown_0.14s_ease-in_forwards]'
                    : 'animate-[dropdownSlideDown_0.15s_cubic-bezier(0.16,1,0.3,1)]'
                }`}
                style={{
                  left: position.left,
                  top: position.top,
                  pointerEvents: closing ? 'none' : undefined
                }}
              >
                <div className="flex items-center justify-between mb-2">
                  <span className="text-xs font-medium text-themed-secondary">
                    {t('modals.theme.colorPicker.pickColor')}
                  </span>
                  {/* closePicker, not a bare setShowPicker: closing through the X has to
                      record the color the picker opened on, or Restore has nothing to
                      undo back to. */}
                  <Button
                    type="button"
                    variant="subtle"
                    size="sm"
                    onClick={closePicker}
                    aria-label={t('common.close')}
                    className="btn-icon-square btn-icon-square--sm pointer-target-44 themed-border-radius-sm"
                  >
                    <X className="w-3 h-3 text-themed-muted" />
                  </Button>
                </div>
                <HexColorPicker color={hexValue} onChange={handleHexChange} />
                <div className="mt-3 space-y-2">
                  <HexColorInput
                    color={hexValue}
                    onChange={handleHexChange}
                    className="themed-input input-search-sm themed-border-radius-sm w-full px-2 font-mono"
                    prefixed
                  />
                  {supportsAlpha && (
                    <div className="flex items-center gap-2">
                      <Percent className="w-3 h-3 text-themed-muted" />
                      {/* The word only. The percentage already sits at the end of the row, and a
                        per-cent glyph is the sole other clue as to what this slider drives. */}
                      <Tooltip
                        content={t('modals.theme.colorPicker.opacity')}
                        position="top"
                        className="flex flex-1"
                      >
                        <Slider
                          min={0}
                          max={100}
                          value={Math.round(alpha * 100)}
                          onChange={(percent: number) => handleAlphaChange(percent / 100)}
                          className="flex-1"
                          aria-label={t('modals.theme.colorPicker.opacity')}
                        />
                      </Tooltip>
                      <span className="text-xs text-themed-muted w-10 text-right">
                        {Math.round(alpha * 100)}%
                      </span>
                    </div>
                  )}
                </div>
              </div>,
              document.body
            )}
        </div>

        {/* Text input */}
        <input
          {...noAutofill}
          type="text"
          value={value}
          onFocus={() => {
            colorBeforeEdit.current = value;
            if (onStart) onStart();
          }}
          onChange={(e) => onChange(e.target.value)}
          onBlur={handleHexBlur}
          aria-invalid={hexInvalid}
          className={`themed-input input-search-sm themed-border-radius-sm w-24 px-2 font-mono${
            hexInvalid ? ' theme-color-hex--invalid' : ''
          }`}
          placeholder=""
        />

        {/* Action buttons */}
        {onCopy && (
          <Tooltip content={t('modals.theme.colorPicker.copyColor')} position="top">
            <Button
              type="button"
              variant="subtle"
              size="sm"
              onClick={() => onCopy(value)}
              aria-label={t('modals.theme.colorPicker.copyColor')}
              className="btn-icon-square btn-icon-square--sm pointer-target-44 themed-border-radius-sm"
            >
              {copiedColor === value ? (
                <Check className="w-3 h-3 icon-success" />
              ) : (
                <Copy className="w-3 h-3 text-themed-muted" />
              )}
            </Button>
          </Tooltip>
        )}

        {/* Restore button - always visible */}
        {onRestore && (
          <Tooltip
            content={
              hasHistory
                ? t('modals.theme.colorPicker.restorePrevious')
                : t('modals.theme.colorPicker.noHistory')
            }
            position="top"
          >
            <Button
              type="button"
              variant="subtle"
              size="sm"
              onClick={onRestore}
              disabled={!hasHistory}
              aria-label={t('modals.theme.colorPicker.restorePrevious')}
              className="btn-icon-square btn-icon-square--sm pointer-target-44 themed-border-radius-sm"
            >
              <RotateCcw className="w-3 h-3 text-themed-muted" />
            </Button>
          </Tooltip>
        )}
      </div>
    </div>
  );
};

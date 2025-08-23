type KbdKey = "command" | "shift" | "ctrl" | "option" | "enter" | "delete" | "escape" | "tab" | "capslock" | "up" | "right" | "down" | "left" | "pageup" | "pagedown" | "home" | "end" | "help" | "space" | "fn" | "win" | "alt";
declare const kbdKeysMap: Record<KbdKey, string>;
declare const kbdKeysLabelMap: Record<KbdKey, string>;
type KbdKeysLabelType = typeof kbdKeysLabelMap;

export { type KbdKey, type KbdKeysLabelType, kbdKeysLabelMap, kbdKeysMap };

"use client";
import {
  useBadge
} from "./chunk-ESVUYDZQ.mjs";

// src/badge.tsx
import { forwardRef } from "@heroui/system";
import { jsx, jsxs } from "react/jsx-runtime";
var Badge = forwardRef((props, ref) => {
  const { Component, children, content, slots, classNames, getBadgeProps } = useBadge({
    ...props
  });
  return /* @__PURE__ */ jsxs("div", { className: slots.base({ class: classNames == null ? void 0 : classNames.base }), children: [
    children,
    /* @__PURE__ */ jsx(Component, { ref, ...getBadgeProps(), children: content })
  ] });
});
Badge.displayName = "HeroUI.Badge";
var badge_default = Badge;

export {
  badge_default
};

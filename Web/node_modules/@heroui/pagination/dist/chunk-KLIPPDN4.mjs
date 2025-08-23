"use client";

// src/pagination-cursor.tsx
import { forwardRef } from "@heroui/system";
import { useDOMRef } from "@heroui/react-utils";
import { jsx } from "react/jsx-runtime";
var PaginationCursor = forwardRef((props, ref) => {
  const { as, activePage, ...otherProps } = props;
  const Component = as || "span";
  const domRef = useDOMRef(ref);
  return /* @__PURE__ */ jsx(Component, { ref: domRef, "aria-hidden": true, ...otherProps, children: activePage });
});
PaginationCursor.displayName = "HeroUI.PaginationCursor";
var pagination_cursor_default = PaginationCursor;

export {
  pagination_cursor_default
};

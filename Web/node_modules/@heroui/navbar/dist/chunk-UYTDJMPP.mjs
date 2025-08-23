"use client";
import {
  useNavbarContext
} from "./chunk-IGERPFKH.mjs";

// src/navbar-content.tsx
import { forwardRef } from "@heroui/system";
import { useDOMRef } from "@heroui/react-utils";
import { clsx } from "@heroui/shared-utils";
import { jsx } from "react/jsx-runtime";
var NavbarContent = forwardRef((props, ref) => {
  var _a;
  const { as, className, children, justify = "start", ...otherProps } = props;
  const Component = as || "ul";
  const domRef = useDOMRef(ref);
  const { slots, classNames } = useNavbarContext();
  const styles = clsx(classNames == null ? void 0 : classNames.content, className);
  return /* @__PURE__ */ jsx(
    Component,
    {
      ref: domRef,
      className: (_a = slots.content) == null ? void 0 : _a.call(slots, { class: styles }),
      "data-justify": justify,
      ...otherProps,
      children
    }
  );
});
NavbarContent.displayName = "HeroUI.NavbarContent";
var navbar_content_default = NavbarContent;

export {
  navbar_content_default
};

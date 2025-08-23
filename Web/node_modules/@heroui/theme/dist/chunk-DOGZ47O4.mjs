import {
  twMergeConfig
} from "./chunk-UFVD3L5A.mjs";

// src/utils/cn.ts
import clsx from "clsx";
import { extendTailwindMerge } from "tailwind-merge";
var twMerge = extendTailwindMerge({ extend: twMergeConfig });
function cn(...inputs) {
  return twMerge(clsx(inputs));
}

export {
  cn
};

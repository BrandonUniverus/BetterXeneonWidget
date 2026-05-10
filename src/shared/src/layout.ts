/**
 * Canonical Xeneon Edge slot sizes. Use these in dev preview and tests.
 * From tools/widgetbuilder-kit/WidgetBuilder/references/security-and-testing-checklists.md
 */
export const XENEON_SLOTS = {
  smallH: { w: 840, h: 344 },
  smallV: { w: 696, h: 416 },
  mediumH: { w: 840, h: 696 },
  mediumV: { w: 696, h: 840 },
  largeH: { w: 1688, h: 696 },
  largeV: { w: 696, h: 1688 },
  extraLargeH: { w: 2536, h: 696 },
  extraLargeV: { w: 696, h: 2536 },
} as const;

export type SlotName = keyof typeof XENEON_SLOTS;

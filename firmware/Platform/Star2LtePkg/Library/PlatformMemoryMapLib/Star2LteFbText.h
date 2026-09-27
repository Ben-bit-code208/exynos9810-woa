/** @file
  Star2LteFbText — direct-framebuffer progress text for the SEC/PEI bring-up
  phase (Exynos 9810 / Galaxy S9+). See Star2LteFbText.c.
**/

#ifndef STAR2LTE_FB_TEXT_H_
#define STAR2LTE_FB_TEXT_H_

#include <Uefi.h>

/**
  Draw one line of ASCII text directly to the panel scanout buffer at the given
  pixel row (white-on-black, 2x scale). MMU-off safe; no boot services used.

  @param[in] PixelRow  Top pixel row (Y) of the text line on the 1440x2960 panel.
  @param[in] Str       NUL-terminated ASCII string (0x20..0x7E; others -> space).
**/
VOID
Star2LteFbText (
  IN UINT32       PixelRow,
  IN CONST CHAR8  *Str
  );

#endif // STAR2LTE_FB_TEXT_H_

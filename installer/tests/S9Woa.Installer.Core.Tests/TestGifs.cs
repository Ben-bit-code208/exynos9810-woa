// SPDX-License-Identifier: BSD-2-Clause-Patent
namespace S9Woa.Installer.Core.Tests;

/// <summary>
/// Tiny synthetic GIFs (made with Pillow for these tests; not the UpdateOS asset,
/// which is Microsoft's and never committed). Expected pixels are Pillow's own
/// decode of the same bytes.
/// </summary>
internal static class TestGifs
{
    /// <summary>
    /// 16x12, two frames of 350 ms, transparency index 0, disposal "leave". Frame 0: a red
    /// square (x 3..8, y 2..7) on transparent. Frame 1 is stored as a sub-rectangle with its
    /// own colour table and adds a green bar (x 10..13) over frame 0.
    /// </summary>
    public const string TwoFrames = "R0lGODlhEAAMAIEAAAAAAP8AAAAAAAAAACH/C05FVFNDQVBFMi4wAwEAAAAh+QQFIwAAACwAAAAAEAAMAAAIJwABCBxIsKDBgwcDKFyIUOBChg0fKmwIQGIAihYxSqTIsaPHjxwDAgAh+QQFIwAAACwDAAAACwAMAIEAAAAA/wAAAAAAAAAIKAABCBwIIIDBAAQHHkSYsODBhg4NQlw48WFDihctJsS4USNBjh8PBgQAOw==";

    /// <summary>20x20 interlaced GIF87a, four greys: index (x/5 + y/5) % 4 of black, 64, 128, 255.</summary>
    public const string Interlaced = "R0lGODdhFAAUAIEAAAAAAEBAQICAgP///ywAAAAAFAAUAEAIbAABCBQYoGBBAQgRDli40ODBhAIYLhwoUOIAigAcBoAoAKNGjhZBSvTokKRBkQw/QrSIEeVEiioTsoTpcObAmAhNPpQ5kubJlRJxRux506HLiz53KiQq8KjOjUAZPrVJ0GjUl0V/8pSaFGrCgAA7";

    public static byte[] Bytes(string b64) => Convert.FromBase64String(b64);
}

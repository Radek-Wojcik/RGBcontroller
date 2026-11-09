# Third-party notices

RGBcontroller itself is MIT licensed (see [LICENSE](LICENSE)). The installer and the release build also ship or use the
following separate programs. They keep their own licenses; nothing here changes them.

## Bundled with the installer

**OpenRGB 1.0rc2** (commit `0fca93e`), unmodified, in `{app}\openrgb`. OpenRGB is licensed under the GNU General Public
License version 2 (or later). Its source code and license are at <https://gitlab.com/CalcProgrammer1/OpenRGB>; the exact
build is identified by the SHA-256 in [third_party/openrgb-pin.json](third_party/openrgb-pin.json). RGBcontroller starts it
as a separate process and talks to it over its SDK socket; the two programs are not linked together. The libraries that come
with that OpenRGB build stay under their own licenses: Qt 5 (LGPL 3 and other terms, <https://www.qt.io/licensing/>), libusb
(LGPL 2.1, <https://libusb.info>), hidapi (<https://github.com/libusb/hidapi>) and PawnIOLib (<https://pawnio.eu>).

## Not bundled, required

**PawnIO** driver, installed by the user from <https://pawnio.eu>. The installer only checks that it is present.
**Microsoft Edge WebView2 Runtime**, a Windows component under Microsoft's terms; the installer only checks that it is present.

## .NET packages and front-end libraries

The .NET build uses Serilog (Apache 2.0), StreamJsonRpc and Nerdbank.Streams (MIT), Vortice.Windows (MIT) and the
Microsoft.Web.WebView2 package (Microsoft's license). The UI is built with React, Vite and TypeScript. The exact versions are in
`Directory.Packages.props` and `ui/package.json`.

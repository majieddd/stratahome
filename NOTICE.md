# Third-party notices

StrataHome's window is a native port of the look of the web app that Strata serves. These parts come from other projects:

## Strata (MIT)

https://github.com/Niko1221/Strata, Copyright (c) 2026 Niko1221 and the Strata contributors, MIT License.

Ported from Strata's `serve/web/` (colour and type tokens, component styles and layout in `tokens.css`, `components.css`, `app.css`;
the Monitor's metrics and how they are drawn in `app.js`; the small Markdown renderer; the sprite's icon paths in `sprite.svg`,
which `tools/make_ui_assets.py` turns into `src/Ui/IconData.g.cs`). StrataHome talks to Strata's local HTTP API
(`/health`, `/metrics`, `/config`, `/settings`, `/load`, `/unload`, `/v1/chat/completions`) and starts Strata's own `serve/server.py`.
StrataHome is not affiliated with Strata or its author.

> MIT License
>
> Copyright (c) 2026 Niko1221 and the Strata contributors
>
> Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files
> (the "Software"), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge,
> publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
> subject to the following conditions: The above copyright notice and this permission notice shall be included in all copies or
> substantial portions of the Software. THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING
> BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
> AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE,
> ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

## Outfit (SIL Open Font License 1.1)

The UI font, as bundled by Strata. `assets/fonts/Outfit-*.ttf` are static instances of its variable font (made by `tools/make_ui_assets.py`);
the licence is in `assets/fonts/OFL.txt`. The fonts are embedded in `StrataHome.exe`.

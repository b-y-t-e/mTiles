# The phone page

What a paired phone loads: a static page, published to GitHub Pages by `.github/workflows/pages.yml`,
talking to mTiles through [tailcat-link](https://github.com/b-y-t-e/tailcat-link)'s relays. No build step
— the modules load as they are. See `docs/DICTATION.md` → *Dictating from a phone* and ADR 0006.

| file | |
|---|---|
| `app.js` | the three levels (workspaces → layout → tile), the dock, the zoom |
| `connection.js` | pairing from the URL fragment, one stored pairing per computer, requests and pushes |
| `dictation.js`, `tap-worklet.js` | hold to talk: PCM on one channel per utterance |
| `markdown.js` | a small markdown reader and a renderer that never uses `innerHTML` |
| `geometry.js` | the layout miniature's rectangles |
| `dom.js` | building elements, the activity marks, icons |
| `vendor/` | the tailcat-link browser client (`VERSION` is its commit) and tweetnacl 1.0.3, with their licences |

**An injected script on this origin is a shell on the paired computer** — the pairing lives in this
origin's IndexedDB. So: the CSP in `index.html` stays strict, nothing is loaded from anywhere else, and no
text from mTiles is ever parsed as HTML.

Develop against a local build: `node site/phone/serve.mjs`, then set `"Phone": { "PageUrl":
"http://localhost:8787/" }` in `settings.json`. Test: `node --test site/phone/test/*.test.mjs`.
Updating the vendored client: copy `clients/browser/src/*.js` from the tailcat-link commit matching the
`Tailcat.Link` package version, and write that commit into `vendor/tailcat-link/VERSION`.

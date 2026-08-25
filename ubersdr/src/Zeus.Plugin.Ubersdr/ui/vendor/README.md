# Vendored

**`opus-decoder.min.js`** — [opus-decoder](https://github.com/eshaz/wasm-audio-decoders)
0.7.11 by Ethan Halsall, **MIT**, compatible with this plugin's
GPL-2.0-or-later.

Vendored rather than fetched: the panel runs under a content policy that has no
reason to allow a CDN, and a plugin that stops working when someone else's host
goes down is not a plugin. The build is self-contained — the WASM is embedded, so
there is no second request for a `.wasm` file.

It is UMD. Imported for side effects from an ES module it takes the `globalThis`
branch and registers itself as `globalThis["opus-decoder"]`, which is how
`ubersdr.es.js` reaches `OpusDecoder`.

Update by replacing the file and the licence note together, and check the API
shape has not moved: `new OpusDecoder()`, `await ready`, `decodeFrames([...])`
returning `{ channelData, samplesDecoded, sampleRate }`, `free()`.

# Offline Board spelling

`english.js` bundles nspell **2.1.5**, is-buffer **2.0.5**, and the American English
Hunspell dictionary from dictionary-en **4.0.0**. The adjacent licenses cover all
redistributed sources. Dictionary-en includes its SCOWL and affix-file notices.
Upstreams: https://github.com/wooorm/nspell, https://github.com/feross/is-buffer,
https://github.com/wooorm/dictionaries.

The Board lazy-loads this ES module from the application's own static assets. No
CDN, external request, backend endpoint, eval, worker or runtime npm install is
needed. We bundle one English dictionary, not multilingual dictionaries or a new
editor. nspell supplies affix-aware checking and suggestions; our own module owns
Markdown exclusions, input behavior and the overlay. Personal words stay in the
current browser/webview profile, not in card data or a remote account.

To rebuild, install the exact packages into a temporary directory, then run:

```powershell
npm install --prefix <temporary-directory> --ignore-scripts --no-audit --no-fund nspell@2.1.5 is-buffer@2.0.5 dictionary-en@4.0.0 esbuild@0.25.12
node VibeRails/wwwroot/assets/spellcheck/build.mjs <temporary-directory>/node_modules
```

The build verifies every version, bundles with esbuild **0.25.12**, and copies
licenses. Keep `english.js` byte-exact (`.gitattributes`). After an upgrade, run the
spelling Node tests and Board browser tests, including the restrictive-CSP case.

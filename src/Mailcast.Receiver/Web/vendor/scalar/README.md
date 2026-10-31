# Vendored: Scalar API reference

`standalone.js.gz` is `dist/browser/standalone.js` from the npm package `@scalar/api-reference` version 1.76.4 (MIT, https://github.com/scalar/scalar), gzipped with `gzip -9` and otherwise unmodified. The receiver embeds it and serves it at `/api/docs/scalar.js` so the API documentation page (`/api/docs`) works with no internet.

`LICENSE` is Scalar's MIT licence, from the repository root.

To update:

    mkdir /tmp/scalar && cd /tmp/scalar && npm pack @scalar/api-reference@VERSION && tar xzf *.tgz
    gzip -9 -c package/dist/browser/standalone.js > src/Mailcast.Receiver/Web/vendor/scalar/standalone.js.gz

then change the version in this file and check `/api/docs` in a browser.

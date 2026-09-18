// Redirects resolution of the application's native dependency packages to the
// agent-owned copies staged by `clawctl setup`.
//
// The agent identity may read package content but may not map it as an
// executable image, so a packaged `.node` fails to load with
// ERR_DLOPEN_FAILED / access denied. Setup mirrors each native-bearing package
// into the agent's own profile; this module makes resolution find those copies.
//
// Whole package directories are redirected, not individual binaries. Once a
// package resolves to its staged copy, the `__dirname` inside it is staged too,
// so the sibling DLLs and helper executables it derives from its own location
// are correct without any further interception.
//
// Redirection is gated on the staged file existing, so a package that was not
// staged keeps resolving to the immutable package exactly as before.
import { createRequire, register } from "node:module";
import { existsSync } from "node:fs";
import { pathToFileURL } from "node:url";
import { join, sep } from "node:path";

const appRoot = process.env.OPENCLAW_NATIVE_APP_ROOT;
const stagedRoot = process.env.OPENCLAW_NATIVE_STAGED_ROOT;

if (appRoot && stagedRoot) {
  const from = join(appRoot, "node_modules") + sep;
  const to = join(stagedRoot, "node_modules") + sep;

  const redirect = (resolved) => {
    if (typeof resolved !== "string") return null;
    // Windows paths are case-insensitive, and the two roots are produced by
    // different components, so the prefix is compared case-insensitively.
    if (resolved.length <= from.length) return null;
    if (resolved.slice(0, from.length).toLowerCase() !== from.toLowerCase()) {
      return null;
    }
    const candidate = to + resolved.slice(from.length);
    return existsSync(candidate) ? candidate : null;
  };

  const require = createRequire(import.meta.url);
  const Module = require("node:module");
  const resolveFilename = Module._resolveFilename;
  Module._resolveFilename = function (request, parent, isMain, options) {
    const resolved = resolveFilename.call(this, request, parent, isMain, options);
    return redirect(resolved) ?? resolved;
  };

  // The CJS patch above cannot see ESM resolution, which is how the
  // application imports several of these packages, and is also what backs
  // `import.meta.resolve` - the way sqlite-vec locates its loadable extension.
  register(pathToFileURL(join(import.meta.dirname, "native-redirect-hooks.mjs")), {
    parentURL: import.meta.url,
    data: { from, to },
  });
}

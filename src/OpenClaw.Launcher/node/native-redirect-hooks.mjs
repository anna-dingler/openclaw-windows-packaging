// The ESM half of the native dependency redirect. See native-redirect.mjs.
//
// Module hooks run on their own thread, so the roots arrive through `data`
// rather than being read from the environment a second time.
import { existsSync } from "node:fs";
import { fileURLToPath, pathToFileURL } from "node:url";

let from = null;
let to = null;

export async function initialize(data) {
  from = data?.from ?? null;
  to = data?.to ?? null;
}

export async function resolve(specifier, context, nextResolve) {
  const result = await nextResolve(specifier, context);
  if (!from || !to || typeof result.url !== "string") {
    return result;
  }

  if (!result.url.startsWith("file:")) {
    return result;
  }

  let path;
  try {
    path = fileURLToPath(result.url);
  } catch {
    return result;
  }

  if (path.length <= from.length) {
    return result;
  }

  if (path.slice(0, from.length).toLowerCase() !== from.toLowerCase()) {
    return result;
  }

  const candidate = to + path.slice(from.length);
  if (!existsSync(candidate)) {
    return result;
  }

  // shortCircuit is required when a hook returns a url it resolved itself.
  return { ...result, url: pathToFileURL(candidate).href, shortCircuit: true };
}

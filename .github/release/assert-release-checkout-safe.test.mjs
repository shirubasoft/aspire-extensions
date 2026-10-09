import assert from "node:assert/strict";
import { mkdir, mkdtemp, rm, symlink, writeFile } from "node:fs/promises";
import { createRequire } from "node:module";
import os from "node:os";
import path from "node:path";
import test from "node:test";
import {
  assertReleaseCheckoutSafe, assertReviewedVersions, metaSearchPlaces, releaseSearchPlaces, reviewedVersions,
} from "./assert-release-checkout-safe.mjs";

const require = createRequire(new URL("./node_modules/semantic-release/package.json", import.meta.url));
const defaults = require("cosmiconfig/dist/defaults.js");

test("pins every release and meta config search place to the installed tooling versions", () => {
  assertReviewedVersions({
    semanticRelease: require("./package.json").version,
    cosmiconfig: require("cosmiconfig/package.json").version,
  });
  assert.deepEqual(releaseSearchPlaces, defaults.getDefaultSearchPlaces("release"));
  assert.deepEqual(metaSearchPlaces, defaults.metaSearchPlaces);
  for (const name of Object.keys(reviewedVersions)) {
    assert.throws(() => assertReviewedVersions({ ...reviewedVersions, [name]: "99.0.0" }),
      /Re-check release configuration discovery/u);
  }
});

test("rejects every discovered config without reading it, including empty files and dangling links", async (t) => {
  const directory = await mkdtemp(path.join(os.tmpdir(), "release-checkout-guard-"));
  t.after(() => rm(directory, { recursive: true, force: true }));
  for (const filename of new Set([...releaseSearchPlaces, ...metaSearchPlaces, ".npmrc", "node_modules"])) {
    const target = path.join(directory, filename);
    await mkdir(path.dirname(target), { recursive: true });
    await writeFile(target, "");
    await assert.rejects(assertReleaseCheckoutSafe(directory), /Untrusted release checkout configuration/u);
    await rm(target);
    await symlink(path.join(directory, "missing"), target);
    await assert.rejects(assertReleaseCheckoutSafe(directory), /Untrusted release checkout configuration/u);
    await rm(target);
  }
  await mkdir(path.join(directory, "node_modules"));
  await assert.rejects(assertReleaseCheckoutSafe(directory), /node_modules/u);
  await rm(path.join(directory, "node_modules"), { recursive: true });
  await assertReleaseCheckoutSafe(directory);
});

test("rejects package metadata and cosmiconfig overrides, and prevents ancestor package lookup", async (t) => {
  const directory = await mkdtemp(path.join(os.tmpdir(), "release-package-guard-"));
  t.after(() => rm(directory, { recursive: true, force: true }));
  const cwd = path.join(directory, "checkout");
  await mkdir(cwd);
  for (const contents of [
    { release: { plugins: ["./credential-reader.cjs"] } },
    { repository: "https://example.test/other/repository" },
    { cosmiconfig: { searchPlaces: ["credential-reader.cjs"], mergeSearchPlaces: false } },
  ]) {
    await writeFile(path.join(cwd, "package.json"), JSON.stringify(contents));
    await assert.rejects(assertReleaseCheckoutSafe(cwd), /configuration: package\.json/u);
  }
  await rm(path.join(cwd, "package.json"));
  await writeFile(path.join(directory, "package.json"), "{}");
  await assert.rejects(assertReleaseCheckoutSafe(cwd), /Untrusted ancestor package\.json/u);
});

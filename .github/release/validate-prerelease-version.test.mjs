import assert from "node:assert/strict";
import test from "node:test";
import { isPublishablePrerelease } from "./validate-prerelease-version.mjs";

test("accepts exact SemVer prereleases", () => {
  assert.equal(isPublishablePrerelease("1.0.0-preview.1"), true);
  assert.equal(isPublishablePrerelease("0.1.0-rc.0"), true);
  assert.equal(isPublishablePrerelease("1.2.3-alpha-beta"), true);
});

test("rejects stable, normalized, and ambiguous package versions", () => {
  assert.equal(isPublishablePrerelease("1.0.0"), false);
  assert.equal(isPublishablePrerelease("01.0.0-preview.1"), false);
  assert.equal(isPublishablePrerelease("1.0.0-preview.01"), false);
  assert.equal(isPublishablePrerelease("1.0.0-preview.1+build.2"), false);
  assert.equal(isPublishablePrerelease("v1.0.0-preview.1"), false);
  assert.equal(isPublishablePrerelease(""), false);
});

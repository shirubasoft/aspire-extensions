import assert from "node:assert/strict";
import path from "node:path";
import test from "node:test";
import { createExtensionReleaseConfig } from "./create-extension-release-config.mjs";

test("creates an independently tagged path-scoped package release", () => {
  const config = createExtensionReleaseConfig({
    extensionPath: "extensions/Shirubasoft.Aspire.Extensions.Kafka",
    packageId: "Shirubasoft.Aspire.Extensions.Kafka",
    tagPrefix: "kafka",
  });

  assert.equal(config.tagFormat, "kafka-v${version}");
  assert.ok(config.plugins.every(([plugin]) => path.isAbsolute(plugin)));
  assert.match(config.plugins[0][0], /path-scoped-conventional-commits\.mjs$/u);
  assert.match(config.plugins[1][0], /@semantic-release[/\\]exec[/\\]index\.js$/u);
  assert.match(config.plugins[2][0], /@semantic-release[/\\]github[/\\]index\.js$/u);
  assert.equal(config.plugins[0][1].paths[0], "extensions/Shirubasoft.Aspire.Extensions.Kafka/**");
  assert.match(config.plugins[1][1].publishCmd, /Shirubasoft\.Aspire\.Extensions\.Kafka/u);
  assert.deepEqual(
    config.plugins[2][1].assets.map((asset) => asset.path),
    [
      "artifacts/Shirubasoft.Aspire.Extensions.Kafka/*.nupkg",
      "artifacts/Shirubasoft.Aspire.Extensions.Kafka/*.snupkg",
    ],
  );
});

test("rejects incomplete extension release metadata", () => {
  assert.throws(
    () => createExtensionReleaseConfig({ extensionPath: "extensions/Kafka" }),
    /required/u,
  );
});

test("publishes a related package family from one extension artifact", () => {
  const config = createExtensionReleaseConfig({
    extensionPath: "extensions/Shirubasoft.Aspire.Extensions.Multirepo",
    packageId: "Shirubasoft.Aspire.Extensions.Multirepo",
    packages: [
      { id: "Shirubasoft.Aspire.Extensions.Multirepo", symbols: true },
      { id: "Shirubasoft.Aspire.Extensions.Multirepo.Templates", symbols: false },
    ],
    tagPrefix: "multirepo",
  });

  const releaseCommands = config.plugins[1][1];
  assert.match(
    releaseCommands.verifyReleaseCmd,
    /Multirepo:symbols;Shirubasoft\.Aspire\.Extensions\.Multirepo\.Templates/u,
  );
  assert.match(releaseCommands.publishCmd, /publish-release-assets\.sh/u);
  assert.match(
    releaseCommands.publishCmd,
    /Multirepo:symbols;Shirubasoft\.Aspire\.Extensions\.Multirepo\.Templates/u,
  );
});

test("rejects invalid package family metadata", () => {
  assert.throws(
    () => createExtensionReleaseConfig({
      extensionPath: "extensions/Multirepo",
      packageId: "Multirepo",
      packages: [{ id: "Multirepo" }],
      tagPrefix: "multirepo",
    }),
    /symbols boolean/u,
  );
});

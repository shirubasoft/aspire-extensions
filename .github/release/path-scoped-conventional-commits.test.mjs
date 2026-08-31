import assert from "node:assert/strict";
import test from "node:test";
import {
  filterCommitsByPath,
  pathMatchesScope,
} from "./path-scoped-conventional-commits.mjs";

test("matches exact files and directory scopes", () => {
  assert.equal(pathMatchesScope("Directory.Packages.props", ["Directory.Packages.props"]), true);
  assert.equal(
    pathMatchesScope(
      "extensions/Shirubasoft.Aspire.Extensions.Kafka/src/Kafka.cs",
      ["extensions/Shirubasoft.Aspire.Extensions.Kafka/**"],
    ),
    true,
  );
  assert.equal(
    pathMatchesScope("extensions/Another/README.md", ["extensions/Shirubasoft.Aspire.Extensions.Kafka/**"]),
    false,
  );
});

test("keeps commit order while removing commits outside the package scope", async () => {
  const commits = [{ hash: "one" }, { hash: "two" }, { hash: "three" }];
  const files = new Map([
    ["one", ["README.md"]],
    ["two", ["extensions/Shirubasoft.Aspire.Extensions.Kafka/README.md"]],
    ["three", ["Directory.Packages.props"]],
  ]);

  const filtered = await filterCommitsByPath(
    commits,
    [
      "extensions/Shirubasoft.Aspire.Extensions.Kafka/**",
      "Directory.Packages.props",
    ],
    { resolveFiles: async (hash) => files.get(hash) },
  );

  assert.deepEqual(filtered, [commits[1], commits[2]]);
});

test("requires an explicit scope", async () => {
  await assert.rejects(
    filterCommitsByPath([], []),
    /requires at least one path/u,
  );
});

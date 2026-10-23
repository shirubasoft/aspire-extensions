import assert from "node:assert/strict";
import test from "node:test";
import {
  commitScope,
  extensionScope,
  filterExtensionCommits,
  isWithinDirectory,
} from "./extension-scoped-conventional-commits.mjs";

const kafka = "extensions/Shirubasoft.Aspire.Extensions.Kafka";
const cloudflareTunnels = "extensions/Shirubasoft.Aspire.Extensions.CloudflareTunnels";
const sharedPaths = ["Directory.Build.props", "Directory.Packages.props", "global.json"];

async function filterKafkaCommits(commits) {
  const files = new Map(commits.map((commit) => [commit.hash, commit.files]));
  return filterExtensionCommits(commits, { extensionPath: kafka, sharedPaths }, {
    resolveFiles: async (hash) => files.get(hash),
    resolveExtensionPaths: async () => [kafka, cloudflareTunnels],
  });
}

test("matches files inside a directory", () => {
  assert.equal(isWithinDirectory(`${kafka}/src/Kafka.cs`, kafka), true);
  assert.equal(isWithinDirectory(`${kafka}.Extras/README.md`, kafka), false);
  assert.equal(isWithinDirectory(`${cloudflareTunnels}/README.md`, kafka), false);
});

test("derives an extension's commit scope from its folder name", () => {
  assert.equal(extensionScope(kafka), "kafka");
  assert.equal(extensionScope(`${cloudflareTunnels}/`), "cloudflare-tunnels");
});

test("reads the scope from a Conventional Commits header", () => {
  assert.equal(commitScope("fix(cloudflare-tunnels)!: drop routes\n\nBody (with parentheses)"), "cloudflare-tunnels");
  assert.equal(commitScope("feat!: rebuild repository"), undefined);
  assert.equal(commitScope("Merge pull request #26"), undefined);
});

test("keeps commits that touch the extension folder in order", async () => {
  const commits = [
    { hash: "one", message: "docs: update README", files: ["README.md"] },
    { hash: "two", message: "fix(cloudflare-tunnels): touch Kafka too", files: [`${kafka}/README.md`] },
    { hash: "three", message: "fix(kafka): retry", files: [`${kafka}/src/Kafka.cs`] },
  ];

  assert.deepEqual(await filterKafkaCommits(commits), [commits[1], commits[2]]);
});

test("keeps shared input changes that do not name another extension", async () => {
  const commits = [
    { hash: "deps", message: "fix(deps): update Aspire", files: ["Directory.Packages.props", `${cloudflareTunnels}/README.md`] },
    { hash: "unscoped", message: "feat!: rebuild repository", files: ["global.json"] },
    { hash: "own", message: "fix(kafka): pin the client", files: ["Directory.Packages.props"] },
  ];

  assert.deepEqual(await filterKafkaCommits(commits), commits);
});

test("drops shared input changes scoped to another extension", async () => {
  const commits = [
    { hash: "with-folder", message: "fix(cloudflare-tunnels): wait for Helm", files: ["Directory.Packages.props", `${cloudflareTunnels}/src/Steps.cs`] },
    { hash: "shared-only", message: "fix(cloudflare-tunnels): bump the preview pin", files: ["Directory.Packages.props"] },
  ];

  assert.deepEqual(await filterKafkaCommits(commits), []);
});

test("requires the extension path and shared inputs", async () => {
  await assert.rejects(
    filterExtensionCommits([], { extensionPath: kafka }),
    /requires extensionPath and sharedPaths/u,
  );
});

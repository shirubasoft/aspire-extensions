import assert from "node:assert/strict";
import { mkdir, mkdtemp, rm, symlink, writeFile } from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import test from "node:test";
import { assertReleaseWorkspaceSafe, assertReviewedVersions, reviewedVersions } from "./assert-release-workspace-safe.mjs";

test("requires a .git directory and rejects every other workspace entry", async (t) => {
  const directory = await mkdtemp(path.join(os.tmpdir(), "release-workspace-guard-"));
  t.after(() => rm(directory, { recursive: true, force: true }));
  await assert.rejects(assertReleaseWorkspaceSafe(directory), /only a \.git directory/u);
  await mkdir(path.join(directory, ".git"));
  await assertReleaseWorkspaceSafe(directory);
  for (const filename of ["unknown-input", "release.config.cjs", "global.json", "NuGet.Config", "Directory.Build.props", "node_modules"]) {
    const target = path.join(directory, filename);
    await writeFile(target, "");
    await assert.rejects(assertReleaseWorkspaceSafe(directory), /only a \.git directory/u);
    await rm(target);
    await symlink(path.join(directory, "missing"), target);
    await assert.rejects(assertReleaseWorkspaceSafe(directory), /only a \.git directory/u);
    await rm(target);
  }
  await rm(path.join(directory, ".git"), { recursive: true });
  await symlink(path.join(directory, "missing"), path.join(directory, ".git"));
  await assert.rejects(assertReleaseWorkspaceSafe(directory), /only a \.git directory/u);
});

test("rejects tool configuration throughout the physical ancestor chain", async (t) => {
  const directory = await mkdtemp(path.join(os.tmpdir(), "release-ancestor-guard-"));
  t.after(() => rm(directory, { recursive: true, force: true }));
  const parent = path.join(directory, "work", "repo");
  const workspace = path.join(parent, "release-publish");
  await mkdir(path.join(workspace, ".git"), { recursive: true });
  const alias = path.join(directory, "alias");
  await symlink(workspace, alias);
  for (const ancestor of [directory, path.dirname(parent), parent]) {
    for (const filename of ["package.json", "global.json", "NuGet.Config", "nuget.config", "Directory.Build.props", "Directory.Build.targets", "Directory.Build.rsp"]) {
      await writeFile(path.join(ancestor, filename), "");
      await assert.rejects(assertReleaseWorkspaceSafe(alias), /Untrusted ancestor configuration/u);
      await rm(path.join(ancestor, filename));
    }
  }
  await mkdir(path.join(parent, ".nuget"));
  await writeFile(path.join(parent, ".nuget/nuget.config"), "");
  await assert.rejects(assertReleaseWorkspaceSafe(workspace), /Untrusted ancestor NuGet configuration/u);
  await rm(path.join(parent, ".nuget"), { recursive: true });
  await assertReleaseWorkspaceSafe(alias);
});

test("requires a review when the locked release tooling versions change", () => {
  for (const name of Object.keys(reviewedVersions)) {
    assert.throws(() => assertReviewedVersions({ ...reviewedVersions, [name]: "99.0.0" }),
      /Re-check empty-workspace release behavior/u);
  }
});

import { createExtensionReleaseConfig } from "../../.github/release/create-extension-release-config.mjs";

export default createExtensionReleaseConfig({
  extensionPath: "extensions/Shirubasoft.Aspire.Extensions.TestProjects",
  packageId: "Shirubasoft.Aspire.Extensions.TestProjects",
  tagPrefix: "test-projects",
});

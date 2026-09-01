import { createExtensionReleaseConfig } from "../../.github/release/create-extension-release-config.mjs";

export default createExtensionReleaseConfig({
  extensionPath: "extensions/Shirubasoft.Aspire.Extensions.ResourceGroups",
  packageId: "Shirubasoft.Aspire.Extensions.ResourceGroups",
  tagPrefix: "resource-groups",
});

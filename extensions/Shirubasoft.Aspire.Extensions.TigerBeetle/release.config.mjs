import { createExtensionReleaseConfig } from "../../.github/release/create-extension-release-config.mjs";

export default createExtensionReleaseConfig({
  extensionPath: "extensions/Shirubasoft.Aspire.Extensions.TigerBeetle",
  packageId: "Shirubasoft.Aspire.Hosting.TigerBeetle",
  tagPrefix: "tiger-beetle",
});

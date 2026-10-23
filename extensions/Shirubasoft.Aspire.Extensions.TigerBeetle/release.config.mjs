import { createExtensionReleaseConfig } from "../../eng/release/create-extension-release-config.mjs";

export default createExtensionReleaseConfig({
  extensionPath: "extensions/Shirubasoft.Aspire.Extensions.TigerBeetle",
  packageId: "Shirubasoft.Aspire.Hosting.TigerBeetle",
  tagPrefix: "tiger-beetle",
});

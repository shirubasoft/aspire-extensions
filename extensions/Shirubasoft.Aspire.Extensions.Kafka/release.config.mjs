import { createExtensionReleaseConfig } from "../../.github/release/create-extension-release-config.mjs";

export default createExtensionReleaseConfig({
  extensionPath: "extensions/Shirubasoft.Aspire.Extensions.Kafka",
  packageId: "Shirubasoft.Aspire.Extensions.Kafka",
  tagPrefix: "kafka",
});

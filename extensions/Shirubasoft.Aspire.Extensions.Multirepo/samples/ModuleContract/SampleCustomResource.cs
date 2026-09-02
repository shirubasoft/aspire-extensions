using Aspire.Hosting.ApplicationModel;

namespace ModularSample.ModuleContract;

/// <summary>A minimal custom Aspire resource used by the modular AppHost sample.</summary>
public sealed class SampleCustomResource(string name) : Resource(name);

using System;
using System.IO;
using System.Text.Json;

File.WriteAllText(Environment.GetEnvironmentVariable("SDK_PROBE_FILE")!, JsonSerializer.Serialize(new
{
    githubToken = Environment.GetEnvironmentVariable("GITHUB_TOKEN"),
    nugetApiKey = Environment.GetEnvironmentVariable("NUGET_API_KEY"),
}));

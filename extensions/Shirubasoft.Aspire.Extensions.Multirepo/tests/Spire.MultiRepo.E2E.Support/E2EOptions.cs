namespace Spire.MultiRepo.E2E.Support;

internal static partial class Program
{
    private sealed record E2EOptions(
        string? RepositoryRoot,
        string? AspirePath,
        string? ContainerRuntime,
        bool KeepTemporary)
    {
        public static E2EOptions Parse(IReadOnlyList<string> args)
        {
            string? repositoryRoot = null;
            string? aspirePath = null;
            string? containerRuntime = null;
            var keepTemporary = false;
            for (var index = 0; index < args.Count; index++)
            {
                switch (args[index])
                {
                    case "--repository-root":
                        repositoryRoot = ReadValue(args, ref index, "--repository-root");
                        break;
                    case "--aspire-path":
                        aspirePath = ReadValue(args, ref index, "--aspire-path");
                        break;
                    case "--container-runtime":
                        containerRuntime = ReadValue(args, ref index, "--container-runtime");
                        if (containerRuntime is not ("docker" or "podman"))
                        {
                            throw new ArgumentException("--container-runtime must be 'docker' or 'podman'.");
                        }
                        break;
                    case "--keep-temporary":
                        keepTemporary = true;
                        break;
                    default:
                        throw new ArgumentException($"Unknown option '{args[index]}'.");
                }
            }

            return new E2EOptions(
                repositoryRoot is null ? null : Path.GetFullPath(repositoryRoot),
                aspirePath,
                containerRuntime,
                keepTemporary);
        }

        private static string ReadValue(IReadOnlyList<string> args, ref int index, string option)
        {
            index++;
            if (index >= args.Count || string.IsNullOrWhiteSpace(args[index]))
            {
                throw new ArgumentException($"{option} requires a value.");
            }

            return args[index];
        }
    }
}

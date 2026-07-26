using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using Npgsql;

namespace MemSrv.Core;

/// <summary>
/// Append-only operator configuration for one binding's prospective routes.
/// Replacing policy inserts a new version; established streams are never edited.
/// </summary>
public sealed class CaptureRoutePolicyStore(string connectionString)
{
    public static string NormalizeRemoteForPolicy(string value) =>
        CaptureRouteResolver.NormalizeRemote(value)
        ?? throw new ArgumentException($"Remote '{value}' is not a supported repository remote.");

    public static string NormalizeDirectoryForPolicy(string value) =>
        CaptureRouteResolver.NormalizeDirectoryForPolicy(value);

    public async Task<Guid> ReplaceAsync(
        string stableName,
        CaptureRoutingPolicy policy,
        CancellationToken cancellationToken = default)
    {
        CaptureLedger.Require(stableName, nameof(stableName));
        ValidatePolicy(policy);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        Guid? bindingUuid = await connection.QuerySingleOrDefaultAsync<Guid?>(
            "SELECT binding_uuid FROM capture_source_bindings WHERE stable_name = @stableName",
            new { stableName }, transaction);
        if (bindingUuid is null)
        {
            throw new InvalidOperationException($"Capture binding '{stableName}' does not exist.");
        }

        foreach (var mapping in policy.SpecialNamespaces)
        {
            if (mapping.Namespace.StartsWith("repo/", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Repository namespace '{mapping.Namespace}' must be authorized " +
                    "by an allowed repository pattern, not a special alias.");
            }
            if (IsReservedNamespace(mapping.Namespace))
            {
                throw new InvalidOperationException(
                    $"Reserved namespace '{mapping.Namespace}' cannot be a capture route target.");
            }
            bool exists = await connection.ExecuteScalarAsync<bool>(
                "SELECT EXISTS (SELECT 1 FROM namespaces WHERE name = @Namespace)",
                new { mapping.Namespace }, transaction);
            if (!exists)
            {
                throw new InvalidOperationException(
                    $"Special namespace '{mapping.Namespace}' must already exist.");
            }
        }

        Guid policyUuid = await connection.ExecuteScalarAsync<Guid>(
            """
            INSERT INTO capture_route_policies
              (binding_uuid, allowed_repository_patterns, remote_overrides,
               directory_routes, special_namespaces)
            VALUES
              (@bindingUuid, @AllowedRepositoryPatterns, CAST(@RemoteOverrides AS jsonb),
               CAST(@DirectoryRoutes AS jsonb), CAST(@SpecialNamespaces AS jsonb))
            RETURNING policy_uuid
            """,
            new
            {
                bindingUuid,
                AllowedRepositoryPatterns = policy.AllowedRepositoryPatterns.ToArray(),
                RemoteOverrides = JsonSerializer.Serialize(
                    policy.RemoteOverrides, CaptureLedger.JsonOptions),
                DirectoryRoutes = JsonSerializer.Serialize(
                    policy.DirectoryRoutes, CaptureLedger.JsonOptions),
                SpecialNamespaces = JsonSerializer.Serialize(
                    policy.SpecialNamespaces, CaptureLedger.JsonOptions)
            }, transaction);
        await transaction.CommitAsync(cancellationToken);
        return policyUuid;
    }

    private static void ValidatePolicy(CaptureRoutingPolicy policy)
    {
        if (policy.AllowedRepositoryPatterns.Any(pattern =>
                string.IsNullOrWhiteSpace(pattern) || !pattern.Contains('/', StringComparison.Ordinal)))
        {
            throw new ArgumentException(
                "Allowed repository patterns must be nonblank owner/name patterns.");
        }
        RequireUnique(
            policy.SpecialNamespaces.Select(item => item.Alias),
            "special namespace aliases");
        RequireUnique(
            policy.RemoteOverrides.Select(item => item.NormalizedRemote),
            "remote override keys");
        RequireUnique(
            policy.DirectoryRoutes.Select(item => item.Directory),
            "directory route paths");

        var aliases = policy.SpecialNamespaces
            .Select(item => item.Alias)
            .ToHashSet(StringComparer.Ordinal);
        foreach (string target in policy.RemoteOverrides.Select(item => item.Target)
                     .Concat(policy.DirectoryRoutes.Select(item => item.Target)))
        {
            if (target.StartsWith("special:", StringComparison.Ordinal))
            {
                if (!aliases.Contains(target["special:".Length..]))
                {
                    throw new ArgumentException(
                        $"Route target '{target}' does not name a configured special alias.");
                }
            }
            else if (!CaptureRouteResolver.TryRepositoryTarget(target, out _))
            {
                throw new ArgumentException(
                    $"Route target '{target}' must be repo/owner/name or special:alias.");
            }
        }
    }

    private static void RequireUnique(IEnumerable<string> values, string label)
    {
        var materialized = values.ToArray();
        if (materialized.Any(string.IsNullOrWhiteSpace)
            || materialized.Distinct(StringComparer.Ordinal).Count() != materialized.Length)
        {
            throw new ArgumentException($"{label} must be nonblank and unique.");
        }
    }

    internal static bool IsReservedNamespace(string value) =>
        string.Equals(value, "memory-system", StringComparison.Ordinal)
        || value.StartsWith("capture/", StringComparison.Ordinal);
}

internal static class CaptureRouteResolver
{
    internal sealed record Result(string Namespace, string Basis);

    public static async Task<Result> ResolveAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CaptureBindingContext binding,
        CaptureRouteEvidence? evidence)
    {
        var remotes = (evidence?.Remotes ?? [])
            .Select((remote, position) => new NormalizedRemote(
                remote.Name, NormalizeRemote(remote.Url), position))
            .Where(remote => remote.Value is not null)
            .OrderBy(remote =>
                string.Equals(remote.Name, "origin", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(remote => remote.Position)
            .ToArray();

        foreach (var remote in remotes)
        {
            foreach (var rule in binding.RoutingPolicy.RemoteOverrides)
            {
                if (string.Equals(
                    remote.Value, rule.NormalizedRemote, StringComparison.Ordinal))
                {
                    return await ResolveTargetAsync(
                        connection, transaction, binding.RoutingPolicy, rule.Target, "override");
                }
            }
        }

        var origin = remotes.FirstOrDefault(remote =>
            string.Equals(remote.Name, "origin", StringComparison.OrdinalIgnoreCase));
        if (origin?.Value is { } normalizedOrigin
            && TryRepositoryFromNormalizedRemote(normalizedOrigin, out string repository)
            && IsRepositoryAllowed(binding.RoutingPolicy, repository))
        {
            string targetNamespace = $"repo/{repository}";
            await EnsureRepositoryNamespaceAsync(connection, transaction, targetNamespace);
            return new Result(targetNamespace, "origin");
        }

        if (NormalizeDirectory(evidence?.WorkingDirectory) is { } workingDirectory)
        {
            var directoryRoute = binding.RoutingPolicy.DirectoryRoutes
                .Where(route => IsDirectoryWithin(workingDirectory, route.Directory))
                .OrderByDescending(route => route.Directory.Length)
                .ThenBy(route => route.Directory, StringComparer.Ordinal)
                .FirstOrDefault();
            if (directoryRoute is not null)
            {
                return await ResolveTargetAsync(
                    connection,
                    transaction,
                    binding.RoutingPolicy,
                    directoryRoute.Target,
                    "directory_mapping");
            }
        }

        return new Result("capture/unscoped", "fallback");
    }

    internal static string? NormalizeRemote(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string host;
        string path;
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && !string.IsNullOrWhiteSpace(uri.Host))
        {
            host = uri.Host;
            path = uri.AbsolutePath;
        }
        else
        {
            var match = Regex.Match(
                value,
                @"^(?:[^@\s]+@)?(?<host>[^:\s]+):(?<path>.+)$",
                RegexOptions.CultureInvariant);
            if (!match.Success)
            {
                return null;
            }
            host = match.Groups["host"].Value;
            path = match.Groups["path"].Value;
        }

        string normalizedPath = path.Trim('/').Trim();
        if (normalizedPath.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            normalizedPath = normalizedPath[..^4];
        }
        string[] parts = normalizedPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length < 2
            ? null
            : $"{host.ToLowerInvariant()}/{parts[^2].ToLowerInvariant()}/{parts[^1].ToLowerInvariant()}";
    }

    internal static string NormalizeDirectoryForPolicy(string value) =>
        NormalizeDirectory(value)
        ?? throw new ArgumentException("Directory routes require an absolute path.");

    internal static bool TryRepositoryTarget(string target, out string repository)
    {
        repository = "";
        if (!target.StartsWith("repo/", StringComparison.Ordinal))
        {
            return false;
        }
        string[] parts = target.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3 || parts.Any(part => part is "." or ".."))
        {
            return false;
        }
        repository = $"{parts[1].ToLowerInvariant()}/{parts[2].ToLowerInvariant()}";
        return true;
    }

    private static async Task<Result> ResolveTargetAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CaptureRoutingPolicy policy,
        string target,
        string basis)
    {
        if (target.StartsWith("special:", StringComparison.Ordinal))
        {
            string alias = target["special:".Length..];
            var mapping = policy.SpecialNamespaces.Single(item =>
                string.Equals(item.Alias, alias, StringComparison.Ordinal));
            return new Result(mapping.Namespace, basis);
        }

        if (!TryRepositoryTarget(target, out string repository)
            || !IsRepositoryAllowed(policy, repository))
        {
            throw new InvalidOperationException(
                $"Repository route '{target}' is outside the binding's allowed repository patterns.");
        }
        string targetNamespace = $"repo/{repository}";
        await EnsureRepositoryNamespaceAsync(connection, transaction, targetNamespace);
        return new Result(targetNamespace, basis);
    }

    private static bool TryRepositoryFromNormalizedRemote(
        string normalizedRemote,
        out string repository)
    {
        string[] parts = normalizedRemote.Split('/', StringSplitOptions.RemoveEmptyEntries);
        repository = parts.Length == 3 ? $"{parts[1]}/{parts[2]}" : "";
        return parts.Length == 3;
    }

    private static bool IsRepositoryAllowed(CaptureRoutingPolicy policy, string repository) =>
        policy.AllowedRepositoryPatterns.Any(pattern =>
            Regex.IsMatch(
                repository,
                $"^{Regex.Escape(pattern.ToLowerInvariant()).Replace("\\*", "[^/]*", StringComparison.Ordinal)}$",
                RegexOptions.CultureInvariant));

    private static async Task EnsureRepositoryNamespaceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string targetNamespace) =>
        await connection.ExecuteAsync(
            """
            INSERT INTO namespaces (name, description)
            VALUES (@targetNamespace, 'Capture route derived from repository origin')
            ON CONFLICT (name) DO NOTHING
            """,
            new { targetNamespace }, transaction);

    private static string? NormalizeDirectory(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathRooted(value))
        {
            return null;
        }
        string full = Path.GetFullPath(value);
        return full.Length == 1 ? full : full.TrimEnd(Path.DirectorySeparatorChar);
    }

    private static bool IsDirectoryWithin(string workingDirectory, string routeDirectory) =>
        string.Equals(workingDirectory, routeDirectory, StringComparison.Ordinal)
        || workingDirectory.StartsWith(
            routeDirectory.EndsWith(Path.DirectorySeparatorChar)
                ? routeDirectory
                : routeDirectory + Path.DirectorySeparatorChar,
            StringComparison.Ordinal);

    private sealed record NormalizedRemote(string Name, string? Value, int Position);
}

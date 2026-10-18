using XCL2.App.Models;

namespace XCL2.App.Services;

/// <summary>Keep metadata IDs separate from the actual folder and JAR repair destinations.</summary>
internal sealed record VersionRepairPlan(VersionDetail Libraries, VersionDetail Assets,
    VersionDetail Client, string ClientVersionId, string ClientJarBaseName)
{
    internal static VersionRepairPlan Create(VersionDetail effective, VersionDetail client, string clientVersionId)
    {
        VersionInheritanceResolver.ValidateId(clientVersionId);
        var jarName = string.IsNullOrWhiteSpace(client.Id) ? clientVersionId : client.Id;
        VersionInheritanceResolver.ValidateId(jarName);
        // The downloader uses Id only to choose natives' directory. Never mutate a parsed JSON ID.
        var libraries = new VersionDetail { Id = clientVersionId, Libraries = effective.Libraries };
        return new(libraries, effective, client, clientVersionId, jarName);
    }

    internal static VersionRepairPlan Create(VersionInheritanceResolver.Resolution inheritance)
        => Create(inheritance.Effective, inheritance.ClientLayer.Detail, inheritance.ClientLayer.VersionId);
}

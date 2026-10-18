using System.Collections.Immutable;
using System.IO;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace XCL2.App.Services.Plugins;

internal sealed record PluginPermissionAnalysis(ImmutableArray<string> Required, ImmutableArray<string> Warnings,
    bool HasCustomSettingsPanel);

/// <summary>Reads imported host API calls without loading or executing a plugin. No default grants are added.</summary>
internal static class PluginUpgradePermissions
{
    internal static ImmutableArray<string> Discover(string entry, CancellationToken ct) => Discover(entry, ct, out _);

    internal static ImmutableArray<string> Discover(string entry, CancellationToken ct, out bool hasCustomSettingsPanel)
    {
        var result = Analyze(entry, ct);
        hasCustomSettingsPanel = result.HasCustomSettingsPanel;
        return result.Required;
    }

    internal static PluginPermissionAnalysis Analyze(string entry, CancellationToken ct)
    {
        var hasCustomSettingsPanel = false;
        var required = new HashSet<string>(StringComparer.Ordinal);
        var warnings = new HashSet<string>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Queue<string>(); pending.Enqueue(entry);
        var root = Path.GetDirectoryName(Path.GetFullPath(entry))!;
        // .NET apphost EXEs have no CLR metadata. Their actual imports live in the companion DLL.
        if (Path.GetExtension(entry).Equals(".exe", StringComparison.OrdinalIgnoreCase))
        {
            var companion = Path.ChangeExtension(entry, ".dll");
            if (File.Exists(companion)) pending.Enqueue(companion);
            warnings.Add("EXE 的管道请求和原生代码不能完整静态推断，请核对 plugin.json 中明确声明的权限。");
        }
        while (pending.TryDequeue(out var path))
        {
            ct.ThrowIfCancellationRequested();
            if (!seen.Add(path)) continue;
            using var input = File.OpenRead(path);
            using var pe = new PEReader(input);
            if (!pe.HasMetadata)
            {
                warnings.Add("检测到无托管元数据的程序：" + Path.GetFileName(path) + "；其权限必须在 plugin.json 明确声明。");
                continue;
            }
            var reader = pe.GetMetadataReader();
            foreach (var handle in reader.TypeDefinitions)
            {
                var implementation = reader.GetTypeDefinition(handle);
                var isPlugin = implementation.GetInterfaceImplementations().Any(h =>
                {
                    var contract = reader.GetInterfaceImplementation(h).Interface;
                    if (contract.Kind != HandleKind.TypeReference) return false;
                    var type = reader.GetTypeReference((TypeReferenceHandle)contract);
                    return reader.GetString(type.Namespace) == "XCL2.App.Services.Plugins" && reader.GetString(type.Name) == "IPlugin" && IsHostReference(reader, type);
                });
                if (!isPlugin) continue;
                foreach (var methodHandle in implementation.GetMethods())
                {
                    var method = reader.GetMethodDefinition(methodHandle);
                    if (method.RelativeVirtualAddress == 0 || !reader.GetString(method.Name).Split('.').Last().Equals("CreateSettingsPanel", StringComparison.Ordinal)) continue;
                    // An implementation may return null in Debug or Release, or choose a view at runtime.
                    // Its mere presence is never positive evidence for ui.basic. Ask for an explicit declaration
                    // when it supplies a view; genuine host UI API imports are checked independently below.
                    hasCustomSettingsPanel = true;
                }
            }
            foreach (var handle in reader.MemberReferences)
            {
                var member = reader.GetMemberReference(handle);
                if (member.Parent.Kind != HandleKind.TypeReference) continue;
                var type = reader.GetTypeReference((TypeReferenceHandle)member.Parent);
                var typeNamespace = reader.GetString(type.Namespace);
                var typeName = reader.GetString(type.Name);
                var methodName = reader.GetString(member.Name);
                if (typeNamespace.StartsWith("System.Reflection", StringComparison.Ordinal) || typeNamespace == "System.IO.Pipes" ||
                    typeNamespace == "System" && typeName == "Type" && methodName is ("GetMethod" or "GetMethods" or "InvokeMember"))
                    warnings.Add("检测到反射、动态加载或管道调用，静态扫描可能遗漏权限，请核对插件作者的声明。");
                if (typeNamespace != "XCL2.App.Services.Plugins" || !IsHostReference(reader, type)) continue;
                var permissions = ForMember(typeName, methodName).ToArray();
                required.UnionWith(permissions);
                if (typeName == "PluginLoginBroker" && methodName == "AuthorizeAsync")
                    warnings.Add("登录请求是否需要 network 取决于运行时登录类型；在线登录须明确声明 network。");
                if (typeName == "PluginPermissionService" && methodName == "Demand")
                    warnings.Add("插件会在运行时请求权限，请确认 Demand 使用的权限均已在 plugin.json 声明。");
                if (permissions.Length == 0 && IsPermissionApi(typeName) && !IsPermissionFree(typeName, methodName))
                    warnings.Add("尚不能推断接口 " + typeName + "." + methodName + " 的权限，请在 plugin.json 明确声明。");
            }
            // Generic subscriptions need only their concrete event permission, never every event permission.
            for (var row = 1; row <= reader.GetTableRowCount(TableIndex.MethodSpec); row++)
            {
                var spec = reader.GetMethodSpecification(MetadataTokens.MethodSpecificationHandle(row));
                if (spec.Method.Kind != HandleKind.MemberReference) continue;
                var member = reader.GetMemberReference((MemberReferenceHandle)spec.Method);
                if (member.Parent.Kind != HandleKind.TypeReference || reader.GetString(member.Name) != "Subscribe") continue;
                var type = reader.GetTypeReference((TypeReferenceHandle)member.Parent);
                if (reader.GetString(type.Namespace) != "XCL2.App.Services.Plugins" || reader.GetString(type.Name) != "PluginEventService" || !IsHostReference(reader, type)) continue;
                var arguments = spec.DecodeSignature(new TypeNames(), null);
                if (arguments.Length == 1 && ForEvent(arguments[0]) is { } permission) required.Add(permission);
            }
            foreach (var handle in reader.AssemblyReferences)
            {
                var name = reader.GetString(reader.GetAssemblyReference(handle).Name);
                if (Path.GetFileName(name) != name || name.Contains(':') || name is "." or "..") continue;
                var dependency = Path.Combine(Path.GetDirectoryName(path)!, name + ".dll");
                if (!File.Exists(dependency)) dependency = Path.Combine(root, name + ".dll");
                if (File.Exists(dependency) && name != typeof(IPlugin).Assembly.GetName().Name) pending.Enqueue(dependency);
                else if (name != typeof(IPlugin).Assembly.GetName().Name && !IsFrameworkAssembly(name))
                    warnings.Add("未找到依赖 " + name + "，无法扫描其中的权限调用。");
            }
        }
        if (hasCustomSettingsPanel && !required.Contains("ui.basic"))
            warnings.Add("UnknownSettingsPanel：检测到 CreateSettingsPanel 实现；若返回自定义界面，须明确声明 ui.basic，返回 null 则不需要。");
        return new(required.OrderBy(p => p, StringComparer.Ordinal).ToImmutableArray(),
            warnings.OrderBy(p => p, StringComparer.Ordinal).ToImmutableArray(), hasCustomSettingsPanel);
    }

    private static bool IsFrameworkAssembly(string name) => name is "mscorlib" or "netstandard" or "System" or
        "WindowsBase" or "PresentationCore" or "PresentationFramework" or "Microsoft.CSharp" || name.StartsWith("System.", StringComparison.Ordinal);

    private static bool IsPermissionApi(string type) => type.EndsWith("Service", StringComparison.Ordinal) ||
        type is "PluginLoginBroker" or "PluginInstallWorkspace" || type.StartsWith("IPlugin", StringComparison.Ordinal);

    private static bool IsPermissionFree(string type, string method) => method is ".ctor" or "Dispose" ||
        method.StartsWith("get_", StringComparison.Ordinal) || type is "PluginConfigService" or "PluginPermissionService" or "IPlugin" or "IPluginSettingsPanel" ||
        (type, method) is ("PluginUiService", "GetDialogTarget") or ("PluginEventService", "Subscribe");

    private static bool IsHostReference(MetadataReader reader, TypeReference type) =>
        type.ResolutionScope.Kind == HandleKind.AssemblyReference &&
        reader.GetString(reader.GetAssemblyReference((AssemblyReferenceHandle)type.ResolutionScope).Name) == typeof(IPlugin).Assembly.GetName().Name;

    internal static IEnumerable<string> ForMember(string type, string method)
    {
        if (method is ".ctor" or "Dispose" || method.StartsWith("get_", StringComparison.Ordinal)) return Array.Empty<string>();
        var permission = (type, method) switch
        {
            ("PluginUiService", "RunOnUiAsync" or "RegisterDialog" or "RegisterButton" or "RegisterOption" or "RegisterPanel" or "SubscribeDialogs" or "ShowDialogAsync" or "ShowMessageAsync" or "ConfirmAsync") => "ui.basic",
            ("PluginUiService", "RegisterNavigationPage") => "ui.navigation",
            ("PluginUiService", "RegisterInstanceAction") => "ui.instanceActions",
            ("PluginUiService", "RegisterSetting") => "ui.settings",
            ("PluginUiService", "RegisterBackground") => "ui.background",
            ("PluginComponentService", "Register" or "Inspect" or "Refresh") => "ui.components",
            ("PluginRuntimeService", "RegisterLaunchMiddleware" or "RegisterAvatarRenderer" or "RegisterSkinModelRenderer") => "ui.components",
            ("PluginPreviewService", "RegisterCard" or "UpdateCard" or "SetAppearance") => "ui.preview",
            ("PluginControlService", "GetPagesAsync" or "NavigateAsync") => "ui.navigation",
            ("PluginControlService", "GetWindowAsync" or "UpdateWindowAsync" or "SetWindowStateAsync") => "ui.window",
            ("PluginControlService", "GetAppearanceAsync") => "settings.read",
            ("PluginControlService", "UpdateSettingsAsync") => "settings.write",
            ("PluginControlService", "GetSnapshotAsync" or "SelectAccountAsync" or "SelectInstanceAsync" or "LaunchSelectedAsync" or "CancelLaunchAsync") => "launcher.control",
            ("PluginEventService", "InterceptLaunch") => "launch.modify",
            ("PluginEventService", "InterceptDownload") => "downloads.intercept",
            ("PluginEventService", "InterceptInstanceCreation") => "instances.validate",
            ("PluginProviderService", "RegisterLoginProvider") => "auth.provider",
            ("PluginLoginBroker", "AuthorizeAsync") => "auth.provider",
            ("PluginNetworkService", "GetTextAsync") => "network",
            ("PluginMessageService", "GetPeers" or "Subscribe" or "SendAsync") => "plugin.messaging",
            ("PluginWorldService", "GetSnapshotAsync" or "GetEditSchema" or "GetAsync") => "worlds.read",
            ("PluginWorldService", "UpdateAsync" or "GenerateCopyAsync") => "worlds.write",
            ("PluginInstallWorkspace", "WriteVersionJsonAsync") => "files.instances.write",
            ("IPluginAccounts", "GetSnapshotAsync" or "GetSelectedAsync") => "accounts.read",
            ("IPluginInstances", "GetSnapshotAsync" or "GetDetailsAsync" or "GetSelectedAsync") => "instances.read",
            ("IPluginMods", "GetSnapshotAsync") => "mods.read",
            ("IPluginDownloads", "GetSnapshotAsync") => "downloads.read",
            ("IPluginSettings", "GetSnapshotAsync") => "settings.read",
            ("IPluginEnvironment", "GetSnapshotAsync") => "environment.read",
            ("PluginManagementService", "GetFoldersAsync" or "GetInstanceDirectoriesAsync") => "instances.read",
            ("PluginManagementService", "SetInstanceHiddenAsync" or "RenameInstanceAsync" or "SetInstanceIsolationAsync") => "instances.write",
            ("PluginManagementService", "SetModEnabledAsync") => "mods.write",
            ("PluginManagementService", "GetJavaRuntimesAsync") => "java.read",
            ("PluginManagementService", "SelectJavaAsync") => "java.select",
            ("PluginManagementService", "GetDownloadAsync") => "downloads.read",
            ("PluginManagementService", "PauseDownloadAsync" or "ResumeDownloadAsync" or "CancelDownloadAsync" or "ClearFinishedDownloadsAsync") => "downloads.control",
            ("PluginResourceService", "GetSourcesAsync" or "SearchAsync" or "GetFilesAsync" or "GetGameVersionsAsync") => "resources.read",
            ("PluginResourceService", "InstallAsync") => "resources.install",
            ("PluginProjectService", "ChooseFolderAsync" or "GetOpenProjects" or "GetProject" or "CloseProject" or "GetFiles" or "GetDirectories" or "SearchText" or "SearchRegex" or "ReadRange" or "ReadHead" or "ReadTail" or "ReadText" or "ReadBytes" or "GetHash" or "GetFileInfo" or "FileExists" or "GetExtensionStats" or "GetLargestFiles" or "FindFiles" or "GetFileCount" or "GetTotalBytes" or "GetNewlineStyle" or "GetEncoding" or "ReadJson" or "GetJsonProperties" or "ReadJsonPointer") => "projects.read",
            ("PluginTaskService", "List" or "Find" or "GetActive" or "GetCompleted" or "GetArchived" or "Get" or "Create" or "Rename" or "SetGoal" or "SetProjectRoot" or "SetMode" or "SetNextAction" or "SetNotes" or "AppendNotes" or "Archive" or "AddStep" or "RemoveStep" or "RenameStep" or "SetStepStatus" or "SetStepEvidence" or "ReplaceSteps" or "GetSteps" or "GetPendingSteps" or "GetCompletedSteps" or "Checkpoint" or "GetCheckpoints" or "Pause" or "Resume" or "Complete" or "AddChange" or "GetChanges" or "GetProgress" or "Clone") => "tasks.manage",
            ("PluginQueryService", "GetInstalledInstances" or "GetIncompleteInstances" or "GetHiddenInstances" or "GetVisibleInstances" or "GetVanillaInstances" or "FindInstancesByName" or "FindInstancesByVersion" or "FindInstancesByLoader" or "GetInstance" or "InstanceExists" or "GetInstanceCount" or "GetMinecraftVersions" or "GetLoaders" or "GetInstancePage") => "instances.read",
            ("PluginQueryService", "GetEnabledMods" or "GetDisabledMods" or "FindModsById" or "FindModsByName" or "GetModByFile" or "GetModCount" or "GetModBytes" or "GetLargestMods" or "GetDuplicateModIds" or "GetModPage") => "mods.read",
            ("PluginQueryService", "GetActiveDownloads" or "GetPausedDownloads" or "GetCompletedDownloads" or "GetFailedDownloads" or "GetCanceledDownloads" or "GetInstanceDownloads" or "GetDownload" or "GetDownloadCount") => "downloads.read",
            ("PluginQueryService", "GetOfflineAccounts" or "GetMicrosoftAccounts" or "GetAuthServerAccounts" or "GetGuestAccounts" or "GetAccount" or "FindAccounts" or "GetAccountCount") => "accounts.read",
            _ => null
        };
        if (permission != null) return new[] { permission };
        if ((type, method) is ("PluginProviderService", "RegisterDownloadProvider")) return new[] { "providers.download", "network" };
        if ((type, method) is ("PluginProviderService", "RegisterModProvider")) return new[] { "providers.mod", "network" };
        if ((type, method) is ("PluginProviderService", "RegisterLoaderInstaller")) return new[] { "providers.loader", "network" };
        if ((type, method) is ("PluginManagementService", "RepairInstanceAsync") or ("PluginResourceService", "InstallGameVersionAsync")) return new[] { "instances.write", "resources.install" };
        if ((type, method) is ("PluginProjectService", "OpenDataProject") || (type == "PluginChangeService" && method is ("PrepareWrite" or "PreparePatch" or "PrepareDelete" or "List" or "Get" or "GetBefore" or "GetAfter" or "GetDiff" or "ApplyAsync" or "RollbackAsync"))) return new[] { "projects.read", "projects.write" };
        if ((type, method) is ("PluginInstallWorkspace", "DownloadFileAsync")) return new[] { "files.instances.write", "network" };
        if ((type, method) is ("PluginInstallWorkspace", "RunJavaInstallerAsync")) return new[] { "files.instances.write", "process.install" };
        if ((type, method) is ("PluginUiService", "ImportBackgroundImageAsync")) return new[] { "ui.background", "files.external.read" };
        // AuthorizeAsync depends on request.Kind (offline/online); its exact requirements belong in the manifest.
        return Array.Empty<string>();
    }

    internal static string? ForEvent(string name) => name.Split('.').Last() switch
    {
        "PluginLauncherReady" or "PluginLauncherStopping" => "events.lifecycle",
        "PluginAccountAdded" or "PluginAccountUpdated" or "PluginAccountRemoved" or "PluginAccountSelectionChanged" => "accounts.read",
        "PluginInstanceCreated" or "PluginInstanceRemoved" or "PluginInstanceRenamed" or "PluginInstanceSelectionChanged" or "PluginInstanceVisibilityChanged" or "PluginInstanceCatalogChanged" or "PluginGameStarted" or "PluginGameExited" or "PluginGameCrashed" => "instances.read",
        "PluginDownloadCreated" or "PluginDownloadProgressChanged" or "PluginDownloadCompleted" or "PluginDownloadFailed" or "PluginDownloadCanceled" or "PluginDownloadRemoved" => "downloads.read",
        "PluginModInstalled" or "PluginModRemoved" or "PluginModEnabledChanged" or "PluginModCatalogChanged" => "mods.read",
        "PluginSettingsChanged" => "settings.read",
        "PluginPeerUnavailable" => "plugin.messaging",
        _ => null
    };

    private sealed class TypeNames : ISignatureTypeProvider<string, object?>
    {
        public string GetArrayType(string elementType, ArrayShape shape) => elementType;
        public string GetByReferenceType(string elementType) => elementType;
        public string GetFunctionPointerType(MethodSignature<string> signature) => "";
        public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments) => genericType;
        public string GetGenericMethodParameter(object? genericContext, int index) => "";
        public string GetGenericTypeParameter(object? genericContext, int index) => "";
        public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType;
        public string GetPinnedType(string elementType) => elementType;
        public string GetPointerType(string elementType) => elementType;
        public string GetPrimitiveType(PrimitiveTypeCode typeCode) => "";
        public string GetSZArrayType(string elementType) => elementType;
        public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
        { var type = reader.GetTypeDefinition(handle); return reader.GetString(type.Namespace) + "." + reader.GetString(type.Name); }
        public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
        { var type = reader.GetTypeReference(handle); return reader.GetString(type.Namespace) + "." + reader.GetString(type.Name); }
        public string GetTypeFromSpecification(MetadataReader reader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind) => reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);
    }
}

using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.IO.Compression;
using System.Buffers.Binary;
using XCL2.App.Services.Plugins;

var checks = 0;
Equal(PluginUpgradePermissions.ForMember("PluginUiService", "GetDialogTarget"));
Equal(PluginUpgradePermissions.ForMember("PluginContext", "get_Ui"));
Equal(PluginUpgradePermissions.ForMember("PluginConfigService", "Set"));
Equal(PluginUpgradePermissions.ForMember("PluginPermissionService", "GetSnapshot"));
Equal(PluginUpgradePermissions.ForMember("PluginControlService", "GetWindowAsync"), "ui.window");
Equal(PluginUpgradePermissions.ForMember("IPluginAccounts", "GetSelectedAsync"), "accounts.read");
Equal(PluginUpgradePermissions.ForMember("IPluginInstances", "GetSelectedAsync"), "instances.read");
Equal(PluginUpgradePermissions.ForMember("PluginUiService", "RegisterButton"), "ui.basic");
Equal(PluginUpgradePermissions.ForMember("PluginRuntimeService", "RegisterLaunchMiddleware"), "ui.components");
Equal(PluginUpgradePermissions.ForMember("PluginManagementService", "RepairInstanceAsync"), "instances.write", "resources.install");
Equal(PluginUpgradePermissions.ForMember("PluginProjectService", "ReadText"), "projects.read");
Equal(PluginUpgradePermissions.ForMember("PluginChangeService", "ApplyAsync"), "projects.read", "projects.write");
Equal(PluginUpgradePermissions.ForMember("PluginProviderService", "RegisterModProvider"), "network", "providers.mod");
Equal(PluginUpgradePermissions.ForMember("PluginTaskService", "ReplaceSteps"), "tasks.manage");
Equal(PluginUpgradePermissions.ForMember("UnknownService", "DeleteEverything"));
Equal(new[] { PluginUpgradePermissions.ForEvent("XCL2.App.Services.Plugins.PluginLauncherReady")! }, "events.lifecycle");
if (PluginUpgradePermissions.ForEvent("CustomEvent") != null) throw new Exception("Unknown event granted a permission."); checks++;

var directory = Path.Combine(Path.GetTempPath(), "xcl-upgrade-permission-test-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
try
{
    var entry = Path.Combine(directory, "fixture.dll");
    WriteFixture(entry, Array.Empty<(string, string)>());
    Equal(PluginUpgradePermissions.Discover(entry, CancellationToken.None));
    WriteFixture(entry, new[] { ("PluginControlService", "GetWindowAsync") });
    Equal(PluginUpgradePermissions.Discover(entry, CancellationToken.None), "ui.window");
    WriteFixture(entry, new[] { ("PluginControlService", "GetWindowAsync"), ("PluginUiService", "RegisterButton") });
    Equal(PluginUpgradePermissions.Discover(entry, CancellationToken.None), "ui.basic", "ui.window");
    WriteFixture(entry, new[] { ("PluginUiService", "RegisterButton") }, host: "UnrelatedAssembly");
    Equal(PluginUpgradePermissions.Discover(entry, CancellationToken.None));
    var dependency = Path.Combine(directory, "helper.dll");
    WriteFixture(dependency, new[] { ("PluginProjectService", "ReadText") });
    WriteFixture(entry, Array.Empty<(string, string)>(), dependency: "helper");
    Equal(PluginUpgradePermissions.Discover(entry, CancellationToken.None), "projects.read");
    var apphost = Path.Combine(directory, "agent.exe");
    WriteFixture(apphost, Array.Empty<(string, string)>(), native: true);
    WriteFixture(Path.ChangeExtension(apphost, ".dll"), new[] { ("IPluginAccounts", "GetSelectedAsync"), ("IPluginInstances", "GetSelectedAsync") });
    var apphostAnalysis = PluginUpgradePermissions.Analyze(apphost, CancellationToken.None);
    Equal(apphostAnalysis.Required, "accounts.read", "instances.read");
    if (!apphostAnalysis.Warnings.Any(w => w.Contains("EXE"))) throw new Exception("Native/pipe uncertainty was hidden."); checks++;
    WriteFixture(entry, Array.Empty<(string, string)>(), dependency: "missing-helper");
    if (!PluginUpgradePermissions.Analyze(entry, CancellationToken.None).Warnings.Any(w => w.Contains("missing-helper")))
        throw new Exception("A missing dependency silently appeared complete."); checks++;
    WriteFixture(entry, new[] { ("PluginUiService", "UnknownFutureMethod") });
    if (!PluginUpgradePermissions.Analyze(entry, CancellationToken.None).Warnings.Any(w => w.Contains("UnknownFutureMethod")))
        throw new Exception("An unknown host API silently appeared permission-free."); checks++;
    WriteFixture(entry, Array.Empty<(string, string)>(), subscribedEvent: "PluginSettingsChanged");
    Equal(PluginUpgradePermissions.Discover(entry, CancellationToken.None), "settings.read");
    // Both fixtures genuinely implement IPlugin.CreateSettingsPanel. Their return-null method bodies
    // must not cause a blanket UI request just because one compiler configuration emits more IL.
    WriteFixture(entry, Array.Empty<(string, string)>(), settingsPanelBody: new byte[] { 0x14, 0x2a });
    Equal(PluginUpgradePermissions.Discover(entry, CancellationToken.None, out var releasePanel));
    if (!releasePanel) throw new Exception("Release settings panel fixture was not detected."); checks++;
    WriteFixture(entry, Array.Empty<(string, string)>(), settingsPanelBody: new byte[] { 0x00, 0x14, 0x0a, 0x2b, 0x00, 0x06, 0x2a });
    Equal(PluginUpgradePermissions.Discover(entry, CancellationToken.None, out var debugPanel));
    if (!debugPanel) throw new Exception("Debug settings panel fixture was not detected."); checks++;
    WriteFixture(entry, new[] { ("PluginUiService", "RegisterButton") }, settingsPanelBody: new byte[] { 0x00, 0x14, 0x0a, 0x2b, 0x00, 0x06, 0x2a });
    Equal(PluginUpgradePermissions.Discover(entry, CancellationToken.None), "ui.basic"); // positive host UI API evidence
    using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
    try { PluginUpgradePermissions.Discover(entry, cancelled.Token); throw new Exception("Cancelled scan completed."); }
    catch (OperationCanceledException) { checks++; }
    // The test executable imports no external host APIs and must never receive blanket UI permissions.
    Equal(PluginUpgradePermissions.Discover(Assembly.GetExecutingAssembly().Location, CancellationToken.None));
    var source = Path.Combine(directory, "source"); Directory.CreateDirectory(Path.Combine(source, "bin"));
    File.WriteAllText(Path.Combine(source, "plugin.json"), "{\"permissions\":[\"accounts.read\"]}");
    File.WriteAllText(Path.Combine(source, "Plugin.cs"), "updated source");
    File.WriteAllText(Path.Combine(source, "migration-report.txt"), "updated permissions");
    File.WriteAllText(Path.Combine(source, "bin", "stale.dll"), "stale build");
    var exported = Path.Combine(directory, "updated-source");
    PluginUpgradeExport.ExportDirectory(source, exported, CancellationToken.None, skipBuild: true);
    if (Directory.Exists(Path.Combine(exported, "bin")) || File.ReadAllText(Path.Combine(exported, "Plugin.cs")) != "updated source")
        throw new Exception("Source folder export lost updated files or included stale build output."); checks++;
    var zipPath = Path.Combine(directory, "updated-source.zip");
    PluginUpgradeExport.ExportZip(source, zipPath, CancellationToken.None, skipBuild: true);
    using (var zip = ZipFile.OpenRead(zipPath))
    {
        Equal(zip.Entries.Select(e => e.FullName), "Plugin.cs", "plugin.json", "migration-report.txt");
        using var text = new StreamReader(zip.GetEntry("plugin.json")!.Open());
        if (text.ReadToEnd() != File.ReadAllText(Path.Combine(exported, "plugin.json"))) throw new Exception("ZIP exported a stale manifest."); checks++;
    }
    Reject<IOException>(() => PluginUpgradeExport.ExportDirectory(source, exported, CancellationToken.None));
    Reject<IOException>(() => PluginUpgradeExport.ExportZip(source, zipPath, CancellationToken.None));
    Reject<IOException>(() => PluginUpgradeExport.ExportDirectory(source, Path.Combine(source, "nested"), CancellationToken.None));
    var cancelledExport = Path.Combine(directory, "cancelled.zip");
    Reject<OperationCanceledException>(() => PluginUpgradeExport.ExportZip(source, cancelledExport, cancelled.Token));
    var invalidExport = Path.Combine(directory, "invalid");
    Reject<InvalidDataException>(() => PluginUpgradeExport.ExportDirectory(source, invalidExport, CancellationToken.None,
        validate: _ => throw new InvalidDataException("invalid signed content")));
    if (File.Exists(cancelledExport) || Directory.Exists(invalidExport) || Directory.EnumerateFileSystemEntries(directory, "*.tmp").Any())
        throw new Exception("An interrupted or invalid export left a partial artifact."); checks++;
    if (!PluginUpgradeExport.IsPackage("A.XCL") || !PluginUpgradeExport.IsPackage("A.xclplugin") || PluginUpgradeExport.IsPackage("A.dll"))
        throw new Exception("Package extension classification is wrong."); checks++;
    PluginUpgradeExport.RequireFormat("real.dll", PluginExportFormat.Dll);
    PluginUpgradeExport.RequireFormat("real.exe", PluginExportFormat.Exe);
    PluginUpgradeExport.RequireFormat("real.exe", PluginExportFormat.Xcl);
    Reject<IOException>(() => PluginUpgradeExport.RequireFormat("real.dll", PluginExportFormat.Exe));
    Reject<IOException>(() => PluginUpgradeExport.RequireFormat("real.exe", PluginExportFormat.Dll));
    // Exercise the real upgrade -> manifest -> signature -> XCL flow, without loading plugin code.
    var installed = Path.Combine(directory, "installed"); Directory.CreateDirectory(installed);
    var installedEntry = Path.Combine(installed, "upgrade.exe");
    WriteFixture(installedEntry, Array.Empty<(string, string)>(), native: true);
    WriteFixture(Path.ChangeExtension(installedEntry, ".dll"), new[] { ("IPluginAccounts", "GetSelectedAsync"), ("PluginNetworkService", "GetTextAsync") });
    File.WriteAllText(Path.Combine(installed, ".xcl-plugin.json"), "{}");
    var info = new LoadedPluginInfo { FilePath = installedEntry, Metadata = PluginManifestReader.Legacy("test.upgrade", "Upgrade fixture", "1.0.0") };
    var upgradePath = Path.Combine(directory, "upgrade.xcl");
    string retainedWorkspace;
    using (var upgrade = await PluginUpgradeService.PrepareAsync(info, null, upgradePath, null, CancellationToken.None))
    {
        var upgraded = PluginManifestReader.Read(Path.Combine(upgrade.PayloadDirectory, upgrade.Entry), "")!;
        Equal(upgraded.Permissions, "accounts.read", "network");
        if (upgrade.SourceDirectory != null || !upgrade.PermissionWarning.Contains("可能有权限不准问题"))
            throw new Exception("Binary-only migration invented source or hid incomplete inference."); checks++;
        if (PluginPackageSignature.Verify(upgrade.PayloadDirectory) != upgrade.Fingerprint)
            throw new Exception("Upgrade signature does not match the exported payload."); checks++;
        using var archive = ZipFile.OpenRead(upgradePath);
        if (archive.GetEntry("upgrade.exe") == null || archive.GetEntry("upgrade.dll") == null || archive.GetEntry("plugin.signature.json") == null)
            throw new Exception("Apphost upgrade lost the companion assembly or signature."); checks++;
        retainedWorkspace = upgrade.Workspace;
    }
    if (Directory.Exists(retainedWorkspace) || !File.Exists(upgradePath) || !File.Exists(installedEntry))
        throw new Exception("Result disposal removed original/exported data or leaked its workspace."); checks++;
    Console.WriteLine($"Plugin upgrade permission checks passed: {checks}");
}
finally { Directory.Delete(directory, true); }

void Equal(IEnumerable<string> actual, params string[] expected)
{
    var a = actual.OrderBy(x => x, StringComparer.Ordinal).ToArray();
    var b = expected.OrderBy(x => x, StringComparer.Ordinal).ToArray();
    if (!a.SequenceEqual(b)) throw new Exception($"Expected [{string.Join(", ", b)}], got [{string.Join(", ", a)}].");
    checks++;
}

void Reject<T>(Action action) where T : Exception
{
    try { action(); } catch (T) { checks++; return; }
    throw new Exception("Expected " + typeof(T).Name);
}

static void WriteFixture(string path, (string Type, string Method)[] members, string? host = null, string? dependency = null, string? subscribedEvent = null, byte[]? settingsPanelBody = null, bool native = false)
{
    var metadata = new MetadataBuilder();
    var ilStream = new BlobBuilder();
    metadata.AddModule(0, metadata.GetOrAddString(Path.GetFileName(path)), metadata.GetOrAddGuid(Guid.NewGuid()), default, default);
    metadata.AddAssembly(metadata.GetOrAddString(Path.GetFileNameWithoutExtension(path)), new Version(1, 0, 0, 0), default, default, 0, default);
    metadata.AddTypeDefinition(TypeAttributes.NotPublic, default, metadata.GetOrAddString("<Module>"), default, MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));
    var hostReference = metadata.AddAssemblyReference(metadata.GetOrAddString(host ?? typeof(IPlugin).Assembly.GetName().Name!), new Version(1, 0, 0, 0), default, default, 0, default);
    if (settingsPanelBody != null)
    {
        var runtime = metadata.AddAssemblyReference(metadata.GetOrAddString("System.Runtime"), new Version(8, 0, 0, 0), default, default, 0, default);
        var framework = metadata.AddAssemblyReference(metadata.GetOrAddString("PresentationFramework"), new Version(4, 0, 0, 0), default, default, 0, default);
        var objectType = metadata.AddTypeReference(runtime, metadata.GetOrAddString("System"), metadata.GetOrAddString("Object"));
        var viewType = metadata.AddTypeReference(framework, metadata.GetOrAddString("System.Windows.Controls"), metadata.GetOrAddString("UserControl"));
        var contract = metadata.AddTypeReference(hostReference, metadata.GetOrAddString("XCL2.App.Services.Plugins"), metadata.GetOrAddString("IPlugin"));
        var context = metadata.AddTypeReference(hostReference, metadata.GetOrAddString("XCL2.App.Services.Plugins"), metadata.GetOrAddString("PluginContext"));
        var pluginType = metadata.AddTypeDefinition(TypeAttributes.Public, metadata.GetOrAddString("Fixture"), metadata.GetOrAddString("SettingsPlugin"), objectType, MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));
        metadata.AddInterfaceImplementation(pluginType, contract);
        var methodSignature = new BlobBuilder(); methodSignature.WriteBytes(new byte[] { 0x20, 0x01, 0x12 }); // instance, one parameter, return class
        methodSignature.WriteCompressedInteger((MetadataTokens.GetRowNumber(viewType) << 2) | 1);
        methodSignature.WriteByte(0x12); methodSignature.WriteCompressedInteger((MetadataTokens.GetRowNumber(context) << 2) | 1);
        var localSignature = new BlobBuilder(); localSignature.WriteBytes(new byte[] { 0x07, 0x01, 0x12 }); // local class variable
        localSignature.WriteCompressedInteger((MetadataTokens.GetRowNumber(viewType) << 2) | 1);
        var locals = metadata.AddStandaloneSignature(metadata.GetOrAddBlob(localSignature));
        var code = new BlobBuilder(); code.WriteBytes(settingsPanelBody);
        var body = new MethodBodyStreamEncoder(ilStream).AddMethodBody(new InstructionEncoder(code, new ControlFlowBuilder()), maxStack: 1, localVariablesSignature: locals);
        metadata.AddMethodDefinition(MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.NewSlot | MethodAttributes.HideBySig,
            MethodImplAttributes.IL | MethodImplAttributes.Managed, metadata.GetOrAddString("CreateSettingsPanel"), metadata.GetOrAddBlob(methodSignature), body, MetadataTokens.ParameterHandle(1));
    }
    if (dependency != null) metadata.AddAssemblyReference(metadata.GetOrAddString(dependency), new Version(1, 0, 0, 0), default, default, 0, default);
    var signature = new BlobBuilder(); signature.WriteBytes(new byte[] { 0x20, 0x00, 0x01 }); // instance method, no parameters, void return
    foreach (var member in members)
    {
        var type = metadata.AddTypeReference(hostReference, metadata.GetOrAddString("XCL2.App.Services.Plugins"), metadata.GetOrAddString(member.Type));
        metadata.AddMemberReference(type, metadata.GetOrAddString(member.Method), metadata.GetOrAddBlob(signature));
    }
    if (subscribedEvent != null)
    {
        var type = metadata.AddTypeReference(hostReference, metadata.GetOrAddString("XCL2.App.Services.Plugins"), metadata.GetOrAddString("PluginEventService"));
        var methodSignature = new BlobBuilder(); methodSignature.WriteBytes(new byte[] { 0x30, 0x01, 0x00, 0x01 }); // generic instance, arity 1, no args, void
        var method = metadata.AddMemberReference(type, metadata.GetOrAddString("Subscribe"), metadata.GetOrAddBlob(methodSignature));
        var eventType = metadata.AddTypeReference(hostReference, metadata.GetOrAddString("XCL2.App.Services.Plugins"), metadata.GetOrAddString(subscribedEvent));
        var generic = new BlobBuilder(); generic.WriteBytes(new byte[] { 0x0a, 0x01, 0x12 }); // method instantiation, one class type
        generic.WriteCompressedInteger((MetadataTokens.GetRowNumber(eventType) << 2) | 1);
        metadata.AddMethodSpecification(method, metadata.GetOrAddBlob(generic));
    }
    var image = new BlobBuilder();
    new ManagedPEBuilder(new PEHeaderBuilder(), new MetadataRootBuilder(metadata), ilStream, flags: CorFlags.ILOnly).Serialize(image);
    var bytes = image.ToArray();
    if (native)
    {
        // Apphost-like PE: clear the CLR data-directory slot, leaving a valid native PE image.
        var optional = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(0x3c, 4)) + 24;
        var directoryStart = optional + (BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(optional, 2)) == 0x20b ? 112 : 96);
        bytes.AsSpan(directoryStart + 14 * 8, 8).Clear();
    }
    File.WriteAllBytes(path, bytes);
}

namespace XCL2.App.Services.Plugins
{
    // UI/loader-independent input shape. All migration, metadata, scanning and signing code above is production code.
    internal interface IPlugin { string Id { get; } string DisplayName { get; } string Version { get; } }
    internal sealed class LoadedPluginInfo
    {
        public string FilePath { get; init; } = "";
        public PluginMetadata? Metadata { get; init; }
        public IPlugin? Plugin { get; init; }
    }
}

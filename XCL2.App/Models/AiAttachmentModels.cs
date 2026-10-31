using System;
using System.Text.Json.Serialization;

namespace XCL2.App.Models;

public enum AiAttachmentKind { Image, File, Archive }

/// <summary>Images remain part of the saved conversation; uploaded files live in an isolated, read-only tool scope.</summary>
public sealed class AiAttachment
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public AiAttachmentKind Kind { get; set; }
    public string MediaType { get; set; } = "application/octet-stream";
    public long SizeBytes { get; set; }
    public int ExtractedFileCount { get; set; }
    public string? ImageBase64 { get; set; }
    // This path is display metadata, never authority. The service validates it against its own GUID-based root.
    public string? TemporaryDirectory { get; set; }
    [JsonIgnore] public string Summary => Kind == AiAttachmentKind.Archive
        ? $"ZIP · {ExtractedFileCount} 个文件 · {SizeBytes / 1024d / 1024d:0.##} MB · 临时只读"
        : $"{(Kind == AiAttachmentKind.Image ? "图片" : "文件")} · {SizeBytes / 1024d:0.#} KB";
    public AiAttachment Clone() => (AiAttachment)MemberwiseClone();
}

using System.Buffers.Binary;
using XCL2.App.Services;

static void Check(bool value, string description) { if (!value) throw new Exception(description); }
foreach (var extension in new[] { ".png", ".PNG", ".gif", ".webm", ".mp4", ".webp" })
    Check(HongheAssetPolicy.IsSupportedDownload("export" + extension), "素材扩展名不被接受：" + extension);
foreach (var file in new[] { "image.png.exe", "script.cmd", "payload.html", "", "export.zip" })
    Check(!HongheAssetPolicy.IsSupportedDownload(file), "非素材文件不应下载：" + file);
var png = new byte[24];
new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }.CopyTo(png, 0);
"IHDR"u8.CopyTo(png.AsSpan(12, 4));
BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(16, 4), 64);
BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(20, 4), 64);
HongheAssetPolicy.ValidatePng(png); // Header bounds; actual image decoding is performed by WPF on import.
foreach (var invalid in new[] { Array.Empty<byte>(), new byte[24], png[..16] })
{
    try { HongheAssetPolicy.ValidatePng(invalid); throw new Exception("无效 PNG 被接受"); }
    catch (InvalidDataException) { }
}
BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(16, 4), uint.MaxValue);
try { HongheAssetPolicy.ValidatePng(png); throw new Exception("超大尺寸被接受"); }
catch (InvalidDataException) { }
Console.WriteLine("PASS: 素材扩展名、PNG 签名、截断及尺寸边界。");

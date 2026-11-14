using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Bitiq.Core.Models;
using Bitiq.Core.Services;

namespace Bitiq.Core.Translation;

public sealed record DownloadProgressInfo(
    string ItemName,
    long BytesReceived,
    long TotalBytes,
    double Percent,
    double SpeedBytesPerSec,
    string StatusText);

/// <summary>
/// Kesilirse Range header ile kaldığı yerden devam eden, SHA256 doğrulamalı model & llama.cpp indiricisi.
/// </summary>
public sealed class ModelDownloadService
{
    private static readonly HttpClient DownloadHttpClient = new(new HttpClientHandler
    {
        AllowAutoRedirect = true,
        AutomaticDecompression = DecompressionMethods.None
    })
    {
        Timeout = TimeSpan.FromMinutes(30)
    };

    private readonly SettingsService _settingsService;

    static ModelDownloadService()
    {
        DownloadHttpClient.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "bitiq/1.0");
    }

    public ModelDownloadService(SettingsService settingsService)
    {
        _settingsService = settingsService;
    }

    public string GetModelPath(ModelQuantization quantization)
    {
        var spec = ModelCatalog.GetModelSpec(quantization);
        return Path.Combine(_settingsService.ModelsDirectory, spec.FileName);
    }

    public string GetLlamaBackendDir(string backendKey) =>
        Path.Combine(_settingsService.LlamaDirectory, ModelCatalog.LlamaReleaseTag, backendKey);

    public string GetLlamaServerExePath(string backendKey) =>
        Path.Combine(GetLlamaBackendDir(backendKey), "llama-server.exe");

    public bool IsModelReady(ModelQuantization quantization)
    {
        string path = GetModelPath(quantization);
        if (!File.Exists(path))
            return false;
        var spec = ModelCatalog.GetModelSpec(quantization);
        return new FileInfo(path).Length == spec.ExpectedSizeBytes;
    }

    public bool IsBackendReady(string backendKey) => File.Exists(GetLlamaServerExePath(backendKey));

    public async Task EnsureLocalAssetsAsync(
        ModelQuantization quantization,
        string backendKey,
        IProgress<DownloadProgressInfo>? progress,
        CancellationToken ct)
    {
        Directory.CreateDirectory(_settingsService.ModelsDirectory);
        Directory.CreateDirectory(_settingsService.LlamaDirectory);

        await EnsureLlamaBackendAsync(backendKey, progress, ct).ConfigureAwait(false);
        await EnsureModelAsync(quantization, progress, ct).ConfigureAwait(false);
    }

    public async Task EnsureLlamaBackendAsync(
        string backendKey,
        IProgress<DownloadProgressInfo>? progress,
        CancellationToken ct)
    {
        string targetDir = GetLlamaBackendDir(backendKey);
        string exePath = Path.Combine(targetDir, "llama-server.exe");
        if (File.Exists(exePath))
            return;

        string archiveCacheDir = Path.Combine(_settingsService.LlamaDirectory, "archives");
        Directory.CreateDirectory(archiveCacheDir);
        Directory.CreateDirectory(targetDir);

        var packages = ModelCatalog.GetLlamaPackages(backendKey);
        var downloadedZips = new List<string>();

        foreach (var pkg in packages)
        {
            string zipPath = Path.Combine(archiveCacheDir, pkg.FileName);
            await DownloadWithResumeAndVerifyAsync(pkg, zipPath, progress, ct).ConfigureAwait(false);
            downloadedZips.Add(zipPath);
        }

        progress?.Report(new DownloadProgressInfo(
            "llama.cpp çıkarılıyor",
            0,
            1,
            100,
            0,
            "llama.cpp dosyaları hazırlanıyor…"));

        string tempExtractDir = targetDir + ".extracting";
        if (Directory.Exists(tempExtractDir))
            Directory.Delete(tempExtractDir, true);
        Directory.CreateDirectory(tempExtractDir);

        foreach (string zip in downloadedZips)
        {
            ZipFile.ExtractToDirectory(zip, tempExtractDir, overwriteFiles: true);
        }

        // Zip içinde alt klasör varsa içeriği doğrudan targetDir'e düzleştir
        var exeFiles = Directory.GetFiles(tempExtractDir, "llama-server.exe", SearchOption.AllDirectories);
        if (exeFiles.Length == 0)
            throw new InvalidDataException("İndirilen llama.cpp arşivinde llama-server.exe bulunamadı.");

        string binRoot = Path.GetDirectoryName(exeFiles[0])!;
        foreach (string file in Directory.GetFiles(tempExtractDir, "*", SearchOption.AllDirectories))
        {
            string dest = Path.Combine(targetDir, Path.GetFileName(file));
            if (string.Equals(Path.GetDirectoryName(file), binRoot, StringComparison.OrdinalIgnoreCase) ||
                file.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(file, dest, overwrite: true);
            }
        }

        try { Directory.Delete(tempExtractDir, true); } catch { }
        // Arşiv zip'lerini disk alanı için temizle
        foreach (string zip in downloadedZips)
        {
            try { File.Delete(zip); } catch { }
        }
    }

    public async Task EnsureModelAsync(
        ModelQuantization quantization,
        IProgress<DownloadProgressInfo>? progress,
        CancellationToken ct)
    {
        var spec = ModelCatalog.GetModelSpec(quantization);
        string destPath = Path.Combine(_settingsService.ModelsDirectory, spec.FileName);
        await DownloadWithResumeAndVerifyAsync(spec, destPath, progress, ct).ConfigureAwait(false);
    }

    public async Task DownloadWithResumeAndVerifyAsync(
        DownloadArtifactSpec spec,
        string destinationPath,
        IProgress<DownloadProgressInfo>? progress,
        CancellationToken ct)
    {
        string? parentDir = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(parentDir))
            Directory.CreateDirectory(parentDir);

        if (File.Exists(destinationPath))
        {
            var fi = new FileInfo(destinationPath);
            if (fi.Length == spec.ExpectedSizeBytes)
            {
                progress?.Report(new DownloadProgressInfo(spec.DisplayName, fi.Length, spec.ExpectedSizeBytes, 100, 0,
                    $"{spec.DisplayName} SHA256 doğrulanıyor…"));

                string actualHash = await ComputeSha256Async(destinationPath, ct).ConfigureAwait(false);
                if (string.Equals(actualHash, spec.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
                    return;

                // Bozuk dosyayı sil ve yeniden indir
                File.Delete(destinationPath);
            }
        }

        string partPath = destinationPath + ".part";
        long existingBytes = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;
        if (existingBytes > spec.ExpectedSizeBytes)
        {
            File.Delete(partPath);
            existingBytes = 0;
        }

        const int maxRetries = 5;
        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                existingBytes = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;
                if (existingBytes < spec.ExpectedSizeBytes)
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, spec.Url);
                    if (existingBytes > 0)
                        req.Headers.Range = new RangeHeaderValue(existingBytes, null);

                    using var resp = await DownloadHttpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct)
                        .ConfigureAwait(false);

                    if (existingBytes > 0 && resp.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
                    {
                        // Dosya zaten tamamlanmış olabilir
                    }
                    else
                    {
                        resp.EnsureSuccessStatusCode();

                        bool isPartial = resp.StatusCode == HttpStatusCode.PartialContent;
                        if (!isPartial && existingBytes > 0)
                        {
                            // Sunucu Range desteklemediyse baştan başla
                            existingBytes = 0;
                        }

                        long totalBytes = spec.ExpectedSizeBytes > 0
                            ? spec.ExpectedSizeBytes
                            : (resp.Content.Headers.ContentLength ?? 0) + existingBytes;

                        var mode = (isPartial && existingBytes > 0) ? FileMode.Append : FileMode.Create;
                        await using var networkStream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                        await using var fileStream = new FileStream(partPath, mode, FileAccess.Write, FileShare.None, 262144, useAsync: true);

                        byte[] buffer = new byte[262144];
                        long receivedTotal = existingBytes;
                        long windowBytes = 0;
                        var sw = Stopwatch.StartNew();
                        long lastReportMs = 0;

                        while (true)
                        {
                            int read = await networkStream.ReadAsync(buffer, ct).ConfigureAwait(false);
                            if (read == 0)
                                break;

                            await fileStream.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                            receivedTotal += read;
                            windowBytes += read;

                            long elapsedMs = sw.ElapsedMilliseconds;
                            if (elapsedMs - lastReportMs >= 200)
                            {
                                double speed = elapsedMs > 0 ? (windowBytes * 1000.0 / elapsedMs) : 0;
                                double pct = totalBytes > 0 ? Math.Min(100.0, receivedTotal * 100.0 / totalBytes) : 0;
                                progress?.Report(new DownloadProgressInfo(
                                    spec.DisplayName,
                                    receivedTotal,
                                    totalBytes,
                                    pct,
                                    speed,
                                    $"{spec.DisplayName} indiriliyor… %{pct:F1}"));
                                lastReportMs = elapsedMs;
                            }
                        }

                        await fileStream.FlushAsync(ct).ConfigureAwait(false);
                    }
                }

                // SHA256 doğrulama
                progress?.Report(new DownloadProgressInfo(
                    spec.DisplayName,
                    spec.ExpectedSizeBytes,
                    spec.ExpectedSizeBytes,
                    100,
                    0,
                    $"{spec.DisplayName} SHA256 bütünlüğü doğrulanıyor…"));

                string sha = await ComputeSha256Async(partPath, ct).ConfigureAwait(false);
                if (!string.Equals(sha, spec.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(partPath);
                    throw new InvalidDataException(
                        $"{spec.DisplayName} SHA256 doğrulaması başarısız! Beklenen: {spec.ExpectedSha256}, Gerçek: {sha}");
                }

                File.Move(partPath, destinationPath, overwrite: true);
                return;
            }
            catch (OperationCanceledException)
            {
                throw; // Kullanıcı iptal ettiyse .part dosyasını koru ki daha sonra devam edebilsin
            }
            catch when (attempt < maxRetries)
            {
                await Task.Delay(TimeSpan.FromSeconds(attempt * 2), ct).ConfigureAwait(false);
            }
        }
    }

    public static async Task<string> ComputeSha256Async(string filePath, CancellationToken ct)
    {
        await using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1048576, useAsync: true);
        byte[] hash = await SHA256.HashDataAsync(fs, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

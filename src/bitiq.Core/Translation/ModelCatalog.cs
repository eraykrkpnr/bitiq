using Bitiq.Core.Models;

namespace Bitiq.Core.Translation;

public sealed record DownloadArtifactSpec(
    string Id,
    string DisplayName,
    string Url,
    long ExpectedSizeBytes,
    string ExpectedSha256,
    string FileName);

public static class ModelCatalog
{
    public const string LlamaReleaseTag = "b12378";
    public const string ModelRepoId = "mradermacher/translategemma-4b-it-GGUF";
    public const string HyMt2RepoId = "tencent/Hy-MT2-7B-GGUF";

    public static ModelQuantization ResolveQuantization(LocalModelKind model, ModelQuantization requested) => model switch
    {
        LocalModelKind.HyMt2_7B => ModelQuantization.Q6_K,
        _ => requested == ModelQuantization.Q6_K ? ModelQuantization.Q8_0 : requested
    };

    public static DownloadArtifactSpec GetModelSpec(LocalModelKind model, ModelQuantization quantization)
    {
        ModelQuantization resolved = ResolveQuantization(model, quantization);
        return model switch
        {
            LocalModelKind.HyMt2_7B => new DownloadArtifactSpec(
                Id: "hy-mt2-7b-q6_k",
                DisplayName: "Hy-MT2 7B (Q6_K — Bağlamlı, RTX 3080 10 GB)",
                Url: "https://huggingface.co/tencent/Hy-MT2-7B-GGUF/resolve/main/HY-MT2-7B-Q6_K.gguf",
                ExpectedSizeBytes: 6164482720L,
                ExpectedSha256: "88ef0aba59952a4cfe4be36cb5baf797dbb370bc60e9dcbd7297036021e52831",
                FileName: "HY-MT2-7B-Q6_K.gguf"),

            _ => GetTranslateGemmaSpec(resolved)
        };
    }

    // Geriye dönük uyumluluk: Q6_K doğrudan Hy-MT2 7B'ye, Q8_0/Q4_K_M TranslateGemma 4B'ye eşlenir.
    public static DownloadArtifactSpec GetModelSpec(ModelQuantization quantization) => quantization switch
    {
        ModelQuantization.Q6_K => GetModelSpec(LocalModelKind.HyMt2_7B, ModelQuantization.Q6_K),
        _ => GetTranslateGemmaSpec(quantization)
    };

    private static DownloadArtifactSpec GetTranslateGemmaSpec(ModelQuantization quantization) => quantization switch
    {
        ModelQuantization.Q8_0 => new DownloadArtifactSpec(
            Id: "translategemma-4b-q8_0",
            DisplayName: "TranslateGemma 4B (Q8_0 — Yüksek Kalite)",
            Url: "https://huggingface.co/mradermacher/translategemma-4b-it-GGUF/resolve/main/translategemma-4b-it.Q8_0.gguf",
            ExpectedSizeBytes: 4130417920L,
            ExpectedSha256: "f5006ddf207becaace1668113a61b31cf8b2e03f0f0baa0d1ca37eb8371d4bc1",
            FileName: "translategemma-4b-it.Q8_0.gguf"),

        _ => new DownloadArtifactSpec(
            Id: "translategemma-4b-q4_k_m",
            DisplayName: "TranslateGemma 4B (Q4_K_M — Hızlı ve Hafif)",
            Url: "https://huggingface.co/mradermacher/translategemma-4b-it-GGUF/resolve/main/translategemma-4b-it.Q4_K_M.gguf",
            ExpectedSizeBytes: 2489909760L,
            ExpectedSha256: "81200d03e843d2ec1ece6eeafe7d13cb6e5211e1fcd336ade55790b683a08330",
            FileName: "translategemma-4b-it.Q4_K_M.gguf")
    };

    // Doğrulanmış ggml-org/llama.cpp b12378 Windows x64 paketleri
    public static IReadOnlyList<DownloadArtifactSpec> GetLlamaPackages(string backendKey) => backendKey switch
    {
        "cuda-13.5" =>
        [
            new DownloadArtifactSpec(
                Id: "llama-cuda-13.5",
                DisplayName: "llama.cpp (CUDA 13.5)",
                Url: "https://github.com/ggml-org/llama.cpp/releases/download/b12378/llama-b12378-bin-win-cuda-13.5-x64.zip",
                ExpectedSizeBytes: 202788566L,
                ExpectedSha256: "5302fd5ab50f79b60021b8042678a3bc2b31aa95f5fac40cc7d05acea5fd1264",
                FileName: "llama-b12378-bin-win-cuda-13.5-x64.zip"),
            new DownloadArtifactSpec(
                Id: "cudart-13.5",
                DisplayName: "CUDA 13.5 Çalışma Zamanı (cudart)",
                Url: "https://github.com/ggml-org/llama.cpp/releases/download/b12378/cudart-llama-bin-win-cuda-13.5-x64.zip",
                ExpectedSizeBytes: 426314199L,
                ExpectedSha256: "c2bf4cc202bfbaaa7a0b75f5082457c3811326947bb216059b72839d844e9cba",
                FileName: "cudart-llama-bin-win-cuda-13.5-x64.zip")
        ],
        "cuda-12.4" =>
        [
            new DownloadArtifactSpec(
                Id: "llama-cuda-12.4",
                DisplayName: "llama.cpp (CUDA 12.4)",
                Url: "https://github.com/ggml-org/llama.cpp/releases/download/b12378/llama-b12378-bin-win-cuda-12.4-x64.zip",
                ExpectedSizeBytes: 319404332L,
                ExpectedSha256: "08f5c9951bee8193375473ae260d2f3b81535be7c542da5e1ac10929859c7615",
                FileName: "llama-b12378-bin-win-cuda-12.4-x64.zip"),
            new DownloadArtifactSpec(
                Id: "cudart-12.4",
                DisplayName: "CUDA 12.4 Çalışma Zamanı (cudart)",
                Url: "https://github.com/ggml-org/llama.cpp/releases/download/b12378/cudart-llama-bin-win-cuda-12.4-x64.zip",
                ExpectedSizeBytes: 391443627L,
                ExpectedSha256: "8c79a9b226de4b3cacfd1f83d24f962d0773be79f1e7b75c6af4ded7e32ae1d6",
                FileName: "cudart-llama-bin-win-cuda-12.4-x64.zip")
        ],
        "vulkan" =>
        [
            new DownloadArtifactSpec(
                Id: "llama-vulkan",
                DisplayName: "llama.cpp (Vulkan Yedek)",
                Url: "https://github.com/ggml-org/llama.cpp/releases/download/b12378/llama-b12378-bin-win-vulkan-x64.zip",
                ExpectedSizeBytes: 38116588L,
                ExpectedSha256: "1748d1377abfc802a679116e4e04b01c30b96a1aa285e7bbd06e3078c72a4919",
                FileName: "llama-b12378-bin-win-vulkan-x64.zip")
        ],
        _ =>
        [
            new DownloadArtifactSpec(
                Id: "llama-cpu",
                DisplayName: "llama.cpp (CPU Yedek)",
                Url: "https://github.com/ggml-org/llama.cpp/releases/download/b12378/llama-b12378-bin-win-cpu-x64.zip",
                ExpectedSizeBytes: 25122595L,
                ExpectedSha256: "2bea6ad03073278b66dfb71791510327d6747ba77d4f20fb528ce357df892952",
                FileName: "llama-b12378-bin-win-cpu-x64.zip")
        ]
    };

    public static string ResolvePreferredBackendKey(LocalBackendKind requested, NvidiaDriverInfo driver)
    {
        return requested switch
        {
            LocalBackendKind.Vulkan => "vulkan",
            LocalBackendKind.Cpu => "cpu",
            LocalBackendKind.Cuda => driver.SupportsCuda13_5 ? "cuda-13.5" : "cuda-12.4",
            _ => driver.SupportsCuda13_5
                ? "cuda-13.5"
                : driver.SupportsCuda12_4
                    ? "cuda-12.4"
                    : "vulkan"
        };
    }
}

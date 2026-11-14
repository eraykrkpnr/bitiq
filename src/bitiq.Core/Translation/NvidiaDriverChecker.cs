using System.Runtime.InteropServices;

namespace Bitiq.Core.Translation;

public sealed record NvidiaDriverInfo(
    bool HasNvidiaDriver,
    int CudaDriverVersion,
    string CudaVersionDisplay,
    bool SupportsCuda12_4,
    bool SupportsCuda13_5,
    string? WarningMessage,
    string? UpdateInstructions);

public static class NvidiaDriverChecker
{
    public const int Cuda12_4_MinVersion = 12040; // NVIDIA Windows Sürücü >= 551.61
    public const int Cuda13_5_MinVersion = 13050; // NVIDIA Windows Sürücü >= 620.xx+

    [DllImport("nvcuda.dll", ExactSpelling = true)]
    private static extern int cuDriverGetVersion(out int driverVersion);

    public static NvidiaDriverInfo Check()
    {
        try
        {
            int res = cuDriverGetVersion(out int version);
            if (res != 0 || version <= 0)
            {
                return CreateMissingOrOldResult(0);
            }

            int major = version / 1000;
            int minor = (version % 1000) / 10;
            string display = $"{major}.{minor}";

            bool supports124 = version >= Cuda12_4_MinVersion;
            bool supports135 = version >= Cuda13_5_MinVersion;

            if (!supports124)
            {
                return new NvidiaDriverInfo(
                    HasNvidiaDriver: true,
                    CudaDriverVersion: version,
                    CudaVersionDisplay: display,
                    SupportsCuda12_4: false,
                    SupportsCuda13_5: false,
                    WarningMessage:
                        $"NVIDIA sürücünüz çok eski (CUDA {display}). Yerel CUDA hızlandırması için en az CUDA 12.4 (NVIDIA Sürücü 551.61+) gerekiyor.",
                    UpdateInstructions:
                        "Sürücüyü güncellemek için NVIDIA App'i açın veya https://www.nvidia.com/drivers adresinden güncel Game Ready / Studio sürücüsünü indirip kurun. Güncelleme yapılana kadar yedek Vulkan/CPU motoru kullanılacak.");
            }

            return new NvidiaDriverInfo(
                HasNvidiaDriver: true,
                CudaDriverVersion: version,
                CudaVersionDisplay: display,
                SupportsCuda12_4: true,
                SupportsCuda13_5: supports135,
                WarningMessage: null,
                UpdateInstructions: null);
        }
        catch (DllNotFoundException)
        {
            return CreateMissingOrOldResult(0);
        }
        catch (Exception ex)
        {
            return new NvidiaDriverInfo(
                HasNvidiaDriver: false,
                CudaDriverVersion: 0,
                CudaVersionDisplay: "Yok",
                SupportsCuda12_4: false,
                SupportsCuda13_5: false,
                WarningMessage: $"NVIDIA CUDA sürücüsü okunamadı ({ex.Message}). Yedek olarak Vulkan veya CPU kullanılacak.",
                UpdateInstructions: "RTX 3080/NVIDIA GPU kullanıyorsanız https://www.nvidia.com/drivers adresinden güncel sürücüyü kurun.");
        }
    }

    private static NvidiaDriverInfo CreateMissingOrOldResult(int version) => new(
        HasNvidiaDriver: false,
        CudaDriverVersion: version,
        CudaVersionDisplay: "Yok",
        SupportsCuda12_4: false,
        SupportsCuda13_5: false,
        WarningMessage: "NVIDIA CUDA sürücüsü (nvcuda.dll) bulunamadı. Yerel model yedek Vulkan/CPU ile çalıştırılacak.",
        UpdateInstructions: "RTX 3080 veya NVIDIA ekran kartı kullanıyorsanız https://www.nvidia.com/drivers adresinden en güncel NVIDIA sürücüsünü (551.61 veya üzeri) yükleyin.");
}

using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace MigamBridge.Tray;

/// <summary>버전 매니페스트 JSON의 형태. 예: {"latest":"1.1.0","url":"https://.../MigamBridgeSetup.exe","sha256":"...","notes":"..."}</summary>
public record UpdateManifest(string Latest, string Url, string? Sha256, string? Notes);

/// <summary>업데이트 확인 결과. 호출자가 상황에 맞는 메시지를 보여줄 수 있도록 구분해서 반환한다.</summary>
public enum UpdateCheckResult
{
    /// <summary>Update:ManifestUrl이 비어있어서 확인 자체를 하지 않음 (아직 진짜 최신인지 모름).</summary>
    NotConfigured,
    /// <summary>매니페스트를 확인했고, 지금 버전이 이미 최신임.</summary>
    UpToDate,
    /// <summary>네트워크 오류, 매니페스트 파싱 실패 등으로 확인 자체가 실패함.</summary>
    CheckFailed,
    /// <summary>다운로드했지만 체크섬이 안 맞거나 설치 프로세스 실행에 실패함.</summary>
    DownloadOrInstallFailed,
    /// <summary>새 버전을 찾아 검증까지 마치고 무음 설치를 시작함 - 호출자는 앱을 종료해야 함.</summary>
    Installing
}

/// <summary>
/// 버전 확인 + 완전 자동(백그라운드 무음 설치) 업데이트.
///
/// 동작 순서:
/// 1. Update:ManifestUrl(appsettings.json)에서 최신 버전 정보(JSON)를 받아옴
/// 2. 지금 실행 중인 어셈블리 버전과 비교
/// 3. 더 최신이면 Setup.exe를 임시 폴더에 다운로드
/// 4. 매니페스트에 sha256이 있으면 반드시 검증 - 안 맞으면 절대 실행하지 않고 중단
///    (다운로드 채널이 변조/손상됐을 가능성 - 자동 설치이기 때문에 이 검증이 특히 중요함)
/// 5. "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART"로 조용히 실행
///    (Setup.exe 쪽 AppMutex 설정 덕분에, 지금 떠있는 트레이 앱을 Inno Setup이 알아서 종료시키고 덮어씀)
/// 6. Installing을 반환하면 호출자(TrayAppContext)가 스스로 프로세스를 정리하고 종료해야 함
///    - Setup.exe의 [Run]에 "설치 후 실행" 옵션이 있으니 설치 완료 후 새 버전이 자동으로 다시 뜬다.
/// </summary>
internal static class UpdateChecker
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    public static Version CurrentVersion =>
        System.Reflection.Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0, 0);

    public static async Task<UpdateCheckResult> CheckAndInstallIfNewerAsync(string? manifestUrl, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(manifestUrl))
            return UpdateCheckResult.NotConfigured; // Update:ManifestUrl이 비어있음 - 아직 진짜 최신인지 확인 자체를 안 함

        UpdateManifest? manifest;
        try
        {
            var json = await Http.GetStringAsync(manifestUrl, ct);
            manifest = JsonSerializer.Deserialize<UpdateManifest>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch
        {
            return UpdateCheckResult.CheckFailed; // 네트워크 오류/파싱 오류 - 다음 확인 주기에 재시도하면 됨
        }

        if (manifest is null || string.IsNullOrWhiteSpace(manifest.Url) || !Version.TryParse(manifest.Latest, out var latestVersion))
            return UpdateCheckResult.CheckFailed;

        if (latestVersion <= CurrentVersion)
            return UpdateCheckResult.UpToDate; // 매니페스트를 실제로 확인했고, 이미 최신 버전임

        string downloadedFile = Path.Combine(Path.GetTempPath(), $"MigamBridgeSetup_{manifest.Latest}.exe");
        try
        {
            await using var responseStream = await Http.GetStreamAsync(manifest.Url, ct);
            await using var fileStream = File.Create(downloadedFile);
            await responseStream.CopyToAsync(fileStream, ct);
        }
        catch
        {
            TryDelete(downloadedFile);
            return UpdateCheckResult.DownloadOrInstallFailed;
        }

        if (!string.IsNullOrWhiteSpace(manifest.Sha256) && !VerifyChecksum(downloadedFile, manifest.Sha256))
        {
            // 체크섬 불일치 - 변조/손상 가능성이 있으므로 절대 실행하지 않음
            TryDelete(downloadedFile);
            return UpdateCheckResult.DownloadOrInstallFailed;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = downloadedFile,
                Arguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART",
                UseShellExecute = true
            });
        }
        catch
        {
            return UpdateCheckResult.DownloadOrInstallFailed;
        }

        return UpdateCheckResult.Installing;
    }

    private static bool VerifyChecksum(string filePath, string expectedSha256)
    {
        using var sha256 = SHA256.Create();
        using var stream = File.OpenRead(filePath);
        var hash = sha256.ComputeHash(stream);
        var hex = Convert.ToHexString(hash);
        return string.Equals(hex, expectedSha256.Replace("-", ""), StringComparison.OrdinalIgnoreCase);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* 무시 */ }
    }
}

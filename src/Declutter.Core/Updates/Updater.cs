using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace Declutter.Core.Updates;

public sealed class UpdateException(string message) : Exception(message);

/// <summary>Checks GitHub for a newer release and downloads it, verifying every byte before use.</summary>
public sealed class Updater(HttpClient http)
{
    public async Task<ReleaseInfo?> GetLatestAsync(CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Releases.LatestReleaseApi);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Declutter", "1.0"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) return null;
        return Releases.Parse(await response.Content.ReadAsStringAsync(ct));
    }

    /// <summary>
    /// Downloads <paramref name="asset"/> to <paramref name="destination"/>. The file is rejected (and deleted)
    /// unless it came from GitHub, has the published size, matches GitHub's SHA-256 digest when one is
    /// published, and is a Windows program.
    /// </summary>
    public async Task DownloadAsync(ReleaseAsset asset, string destination, IProgress<double>? progress, CancellationToken ct)
    {
        if (!Uri.TryCreate(asset.DownloadUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            !Releases.TrustedDownloadHosts.Contains(uri.Host))
            throw new UpdateException("The download link doesn't point at GitHub, so it wasn't used.");

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Declutter", "1.0"));
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode) throw new UpdateException($"GitHub answered {(int)response.StatusCode} for the download.");
            if (response.RequestMessage?.RequestUri is { } final && !Releases.TrustedDownloadHosts.Contains(final.Host))
                throw new UpdateException("The download was redirected away from GitHub, so it wasn't used.");

            long expected = asset.Size > 0 ? asset.Size : response.Content.Headers.ContentLength ?? 0;
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long received = 0;
            byte[] header = new byte[2];
            await using (var source = await response.Content.ReadAsStreamAsync(ct))
            await using (var file = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            {
                byte[] buffer = new byte[1 << 16];
                int read;
                while ((read = await source.ReadAsync(buffer, ct)) > 0)
                {
                    if (received < 2) Array.Copy(buffer, 0, header, (int)received, (int)Math.Min(2 - received, read));
                    await file.WriteAsync(buffer.AsMemory(0, read), ct);
                    sha.AppendData(buffer, 0, read);
                    received += read;
                    if (expected > 0) progress?.Report(Math.Min(1, (double)received / expected));
                }
            }

            if (asset.Size > 0 && received != asset.Size)
                throw new UpdateException("The download was incomplete. Please try again.");
            if (asset.Sha256 != null && !Convert.ToHexString(sha.GetHashAndReset()).Equals(asset.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new UpdateException("The downloaded file doesn't match GitHub's fingerprint (SHA-256), so it wasn't used.");
            if (header[0] != (byte)'M' || header[1] != (byte)'Z')
                throw new UpdateException("The download isn't a Windows program, so it wasn't used.");
        }
        catch
        {
            TryDelete(destination);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

/// <summary>
/// Replaces the running program with a downloaded one. Windows won't let a running .exe be overwritten,
/// but it can be renamed: current → .old, new → current. On failure the original is put back.
/// </summary>
public static class SelfReplace
{
    public static string DownloadPathFor(string currentExe) => currentExe + ".download";
    public static string OldPathFor(string currentExe) => currentExe + ".old";

    public static void Swap(string currentExe, string downloaded)
    {
        string old = OldPathFor(currentExe);
        if (!File.Exists(downloaded)) throw new UpdateException("The downloaded update is missing.");
        if (File.Exists(old)) File.Delete(old);

        File.Move(currentExe, old);
        try
        {
            File.Move(downloaded, currentExe);
        }
        catch
        {
            File.Move(old, currentExe); // roll back: never leave the user without a program
            throw;
        }
    }

    /// <summary>Removes the previous version after an update. Safe to call on every start.</summary>
    public static void CleanUp(string currentExe)
    {
        foreach (var leftover in new[] { OldPathFor(currentExe), DownloadPathFor(currentExe) })
        {
            try
            {
                if (File.Exists(leftover)) File.Delete(leftover);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Still running for a moment, or locked: try again next start.
            }
        }
    }
}

using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace Memory.Domain;

/// <summary>
/// Pure transcript-intake pipeline shared by the CLI and the REST endpoint:
/// normalize line endings → redact secrets → hash (dedup key) → gzip.
/// The hash is computed over the REDACTED content — identical raw transcripts
/// always produce identical redactions, so double-fires (PreCompact + SessionEnd)
/// dedup cleanly while the stored bytes never contain unredacted secrets.
/// </summary>
public static class HarvestIntake
{
    public const int MaxTranscriptBytes = 10 * 1024 * 1024;

    public static HarvestPayload Prepare(string transcript)
    {
        var normalized = transcript.Replace("\r\n", "\n", StringComparison.Ordinal);
        var (redacted, redactions) = SecretScanner.Redact(normalized);

        var redactedBytes = Encoding.UTF8.GetBytes(redacted);
        var contentHash = Convert.ToHexStringLower(SHA256.HashData(redactedBytes));

        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
        {
            gzip.Write(redactedBytes);
        }

        var lines = 1;
        foreach (var ch in redacted)
        {
            if (ch == '\n')
            {
                lines++;
            }
        }

        return new HarvestPayload(contentHash, output.ToArray(), redactions, lines, redacted.Length);
    }

    public static string Decompress(byte[] transcriptGzip)
    {
        using var input = new MemoryStream(transcriptGzip);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}

public sealed record HarvestPayload(
    string ContentHash,
    byte[] TranscriptGzip,
    int Redactions,
    int Lines,
    int Chars);

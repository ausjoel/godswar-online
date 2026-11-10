using System.Buffers.Binary;
using System.Text;

namespace Godswar.Server.Networking;

/// <summary>
/// Writes the quest frames the server sends to a file, for diagnosing a client
/// crash that no capture covers.
/// </summary>
/// <remarks>
/// This is a diagnostic: it changes nothing about what is sent, it only records
/// it. The file is inside the server container, so it survives the client dying
/// and can be read afterwards.
/// </remarks>
internal static class QuestFrameTrace
{
    private const string Path = "/tmp/quest-frames.log";
    private static readonly object Gate = new();

    public static void Append(string label, ReadOnlySpan<byte> frame)
    {
        try
        {
            var builder = new StringBuilder(frame.Length * 2 + 40);
            builder.Append(DateTimeOffset.UtcNow.ToString("HH:mm:ss.fff"));
            builder.Append(' ');
            builder.Append(label);
            if (frame.Length > 0)
            {
                builder.Append(" len=");
                builder.Append(frame.Length);
                if (frame.Length >= 4)
                {
                    builder.Append(" op=");
                    builder.Append(
                        BinaryPrimitives.ReadUInt16LittleEndian(frame[2..4]));
                }

                builder.Append(" hex=");
                foreach (var value in frame)
                {
                    builder.Append(value.ToString("X2"));
                }
            }

            builder.Append('\n');
            lock (Gate)
            {
                File.AppendAllText(Path, builder.ToString());
            }
        }
        catch (Exception)
        {
            // A diagnostic must never be able to break the session.
        }
    }
}

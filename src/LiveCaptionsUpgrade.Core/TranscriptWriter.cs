using System.Globalization;
using System.Text;

namespace LiveCaptionsUpgrade.Core;

/// <summary>Appends finished sentences, with timestamps, to a text file. Thread-safe.</summary>
public sealed class TranscriptWriter : IDisposable
{
    private readonly string _folder;
    private readonly object _gate = new();
    private StreamWriter? _writer;

    public TranscriptWriter(string folder)
    {
        _folder = folder;
    }

    /// <summary>The transcript file, or null until the first sentence has been written.</summary>
    public string? FilePath { get; private set; }

    public void Append(string sentence, DateTimeOffset timestamp)
    {
        if (string.IsNullOrWhiteSpace(sentence))
        {
            return;
        }

        lock (_gate)
        {
            if (_writer is null)
            {
                // Created lazily so a session with no speech leaves no empty file behind.
                Directory.CreateDirectory(_folder);
                string name = string.Create(CultureInfo.InvariantCulture, $"transcript_{timestamp:yyyy-MM-dd_HH-mm-ss}.txt");
                FilePath = Path.Combine(_folder, name);
                var stream = new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.Read);
                _writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)) { AutoFlush = true };
            }

            _writer.WriteLine(string.Create(CultureInfo.InvariantCulture, $"[{timestamp:HH:mm:ss}] {sentence.Trim()}"));
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }
}
